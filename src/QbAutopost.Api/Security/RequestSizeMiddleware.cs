using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Endpoints.V2;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-17: the body cap, applied before anything reads the body — before the key check, because refusing a flood
/// should not depend on who is sending it, and before T-909's idempotency hash, which buffers the whole body.
/// <para>
/// Kestrel enforces the same number at the socket (<c>Program.cs</c>), and that is the one that matters in
/// production. This exists because that limit lives in the server rather than the app: under the test host, and
/// behind any host that does not apply it, the cap would otherwise be both untested and absent.
/// </para>
/// <para>
/// T-1212b (dashboard-api-m12 6.1 "Body size"): one route has its own limit, <c>POST /api/v2/statement-extractions</c>
/// (<c>Extraction:MaxBytes</c> plus a multipart allowance), and answers <c>413 file-too-large</c> over it. Kestrel's
/// server limit is the larger of the two (<see cref="ServerLimit"/>), and each request's own limit is set here, so every
/// other route still stops at <c>Api:MaxRequestBodyBytes</c> with today's problem.
/// </para>
/// </summary>
public sealed class RequestSizeMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
{
    /// <summary>What Kestrel must allow at most: the larger of the global and the upload limit.</summary>
    public static long ServerLimit(ApiSettings api, ExtractionSettings extraction) =>
        Math.Max(api.MaxRequestBodyBytes, StatementExtractionEndpoints.BodyLimit(extraction));

    /// <summary>
    /// Kestrel's own limit: <c>Api:MaxRequestBodyBytes</c> while the upload is off, <see cref="ServerLimit"/> only when
    /// <c>Extraction:Enabled</c> is true (M12 final review).
    /// </summary>
    public static long KestrelLimit(ApiSettings api, ExtractionSettings extraction) =>
        extraction.Enabled ? ServerLimit(api, extraction) : api.MaxRequestBodyBytes;

    /// <summary>The body limit of one request.</summary>
    public static long LimitFor(string method, PathString path, ApiSettings api, ExtractionSettings extraction) =>
        IsExtraction(method, path) ? StatementExtractionEndpoints.BodyLimit(extraction) : api.MaxRequestBodyBytes;

    private static bool IsExtraction(string method, PathString path) =>
        HttpMethods.IsPost(method)
        && path.HasValue
        && path.Value!.TrimEnd('/').Equals(ApiRoutes.V2Prefix + StatementExtractionEndpoints.Path, StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems, ILogger<RequestSizeMiddleware> log)
    {
        var s = settings.Value;
        var extraction = IsExtraction(context.Request.Method, context.Request.Path);
        var max = LimitFor(context.Request.Method, context.Request.Path, s.Api, s.Extraction);

        // Tell the server too: a chunked body declares no length, so only the server can stop it mid-flight.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = max;
        }

        if (context.Request.ContentLength > max)
        {
            log.LogWarning(
                "Request {Method} {Path} refused: body of {Bytes} bytes is over the {Max} byte limit",
                context.Request.Method, context.Request.Path.Value, context.Request.ContentLength, max);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await problems.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = extraction
                    ? new()
                    {
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Type = "file-too-large",
                        Title = "File too large",
                        Detail = $"The file may be at most {s.Extraction.MaxBytes} bytes. Nothing was read or stored.",
                    }
                    : new()
                    {
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Title = "Request body too large",
                        Detail = $"The body may be at most {max} bytes (Api:MaxRequestBodyBytes). Send fewer transactions per request.",
                    },
            });
            return;
        }

        await next(context);
    }
}
