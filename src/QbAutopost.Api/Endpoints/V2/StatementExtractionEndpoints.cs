using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.Extraction;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Api.Security;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>
/// Dashboard-api-m12 6 (R-13): <c>POST /api/v2/statement-extractions</c>, one PDF in <c>multipart/form-data</c> part
/// <c>file</c>. Scope <c>requests:submit</c> (<see cref="ApiRoutes.ScopeFor"/>), and a signed-in <b>user</b>: an
/// extraction belongs to a person, so an API key gets 403 whatever it carries. The body is read into memory, bounded
/// by <see cref="RequestSizeMiddleware"/>'s route limit and by <c>Extraction:MaxBytes</c> for the file; nothing touches
/// disk. The rest is <see cref="StatementExtractionService"/>.
/// </summary>
public static class StatementExtractionEndpoints
{
    public const string Path = "/statement-extractions";
    public const string FilePart = "file";

    /// <summary>Room for the multipart boundaries and part headers around the file (6.1 "Body size", builder's call).</summary>
    public const long MultipartAllowance = 64 * 1024;

    /// <summary>The largest body the route accepts: the file limit plus <see cref="MultipartAllowance"/>.</summary>
    public static long BodyLimit(ExtractionSettings settings) => Math.Max(0, settings.MaxBytes) + MultipartAllowance;

    public static IEndpointRouteBuilder MapStatementExtractionEndpoints(this IEndpointRouteBuilder v2)
    {
        v2.MapGroup(Path).RequireDatabase().MapPost(string.Empty, ExtractAsync);
        return v2;
    }

    private static async Task<IResult> ExtractAsync(
        HttpContext http, StatementExtractionService service, IOptions<AppSettings> settings, CancellationToken ct)
    {
        var caller = CallerMiddleware.CallerOf(http);
        if (caller is null || caller.Kind != CallerKind.User)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Signed-in user required",
                detail: "An extraction belongs to a signed-in dashboard user; an API key cannot upload statements.");
        }

        if (!service.IsConfigured)
        {
            return Answer(http, ExtractionOutcome.Unavailable());
        }

        var x = settings.Value.Extraction;
        var (file, problem) = await ReadFileAsync(http.Request, x, ct);
        if (problem is not null)
        {
            return Answer(http, problem);
        }

        var outcome = await service.ExtractAsync(caller.Id, file!.Value.Name ?? "", file.Value.Bytes, ct);
        return Answer(http, outcome);
    }

    private static IResult Answer(HttpContext http, ExtractionOutcome outcome)
    {
        if (outcome.View is { } view)
        {
            return Results.Json(view, SavedRequestJson.Options);
        }

        if (outcome.RetryAfter is { } wait)
        {
            http.Response.Headers.RetryAfter =
                ((long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        return Results.Problem(statusCode: outcome.Status, title: outcome.Title, detail: outcome.Detail, type: outcome.Type);
    }

    /// <summary>
    /// The one <c>file</c> part, into memory and no further than <c>Extraction:MaxBytes</c> + 1 byte. Other parts are
    /// read past and ignored; a second <c>file</c> part is refused, as is a body that is not multipart.
    /// </summary>
    private static async Task<((string? Name, byte[] Bytes)? File, ExtractionOutcome? Problem)> ReadFileAsync(
        HttpRequest request, ExtractionSettings x, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
        {
            return (null, ExtractionOutcome.InvalidRequest(
                "Send multipart/form-data with the PDF in a part named file. Nothing was read or stored."));
        }

        var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = BodyLimit(x) };
        (string? Name, byte[] Bytes)? found = null;
        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, FilePart, StringComparison.Ordinal))
                {
                    continue;
                }

                if (found is not null)
                {
                    return (null, ExtractionOutcome.InvalidRequest(
                        "Send exactly one part named file. Nothing was read or stored."));
                }

                var bytes = await ReadCappedAsync(section.Body, x.MaxBytes, ct);
                if (bytes is null)
                {
                    return (null, ExtractionOutcome.TooManyBytes(x.MaxBytes));
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                    ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                found = (name, bytes);
            }
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith(BodyLimitMessage, StringComparison.Ordinal))
        {
            // Only MultipartReader's BodyLengthLimit is a size problem; its header and line length limits are a
            // malformed body (below).
            return (null, ExtractionOutcome.TooManyBytes(x.MaxBytes));
        }
        catch (InvalidDataException)
        {
            return (null, Malformed());
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The server's own body limit (set per request by RequestSizeMiddleware) stopped a body without a length.
            return (null, ExtractionOutcome.TooManyBytes(x.MaxBytes));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status400BadRequest)
        {
            // Kestrel refused the body itself (bad chunking, or it ended before its Content-Length).
            return (null, Malformed());
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            // The body ended before the closing boundary. A client fault, not ours: no error is logged.
            return (null, Malformed());
        }

        return found is null
            ? (null, ExtractionOutcome.InvalidRequest("No part named file was sent. Nothing was read or stored."))
            : (found, null);
    }

    /// <summary>The start of <c>MultipartReader</c>'s message when <c>BodyLengthLimit</c> is exceeded (and only then).</summary>
    private const string BodyLimitMessage = "Multipart body length limit ";

    private static ExtractionOutcome Malformed() =>
        ExtractionOutcome.InvalidRequest("The multipart body could not be read. Nothing was read or stored.");

    /// <summary>The stream's bytes, or null as soon as it holds more than <paramref name="max"/>.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, long max, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (copy.Length + n > max)
            {
                return null;
            }

            copy.Write(buffer, 0, n);
        }

        return copy.ToArray();
    }
}
