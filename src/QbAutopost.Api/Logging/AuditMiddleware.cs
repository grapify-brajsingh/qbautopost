using System.Text.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Logging;

/// <summary>
/// FR-A-16: one line in <see cref="AuditLog"/> for every authenticated request that changes something.
/// <para>
/// It sits <b>in front of</b> <see cref="ApiKeyMiddleware"/> and records after the pipeline has returned. That is
/// deliberate (handoff trap 17): the key check answers <c>403</c> by returning early, so an audit placed behind it
/// would never see a caller turned away from the posting route — which is the first thing an auditor asks about. The
/// caller is read out of <c>HttpContext.Items</c> afterwards, which the key check fills in before it decides.
/// </para>
/// <para>
/// A request whose key was not recognised is <i>not</i> authenticated and gets no line (SPEC-GAP T-912, Q-64): the
/// 401 is already in the operational log, and a stranger must not be able to grow the money evidence.
/// </para>
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next)
{
    /// <summary>Beyond this, the answer is not a summary worth parsing for an audit field.</summary>
    private const int MaxBodyToRead = 4 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context, AuditLog audit, IClock clock, ILogger<AuditMiddleware> log)
    {
        if (!ApiRoutes.IsMutating(context.Request.Method, context.Request.Path))
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            // The framework's exception handler will clear the response and write its own; anything half-written
            // here must not reach the caller in front of it.
            context.Response.Body = original;
            Record(context, audit, clock, log, StatusCodes.Status500InternalServerError, body: null);
            throw;
        }

        context.Response.Body = original;
        var bytes = buffer.ToArray();
        await original.WriteAsync(bytes, context.RequestAborted);
        Record(context, audit, clock, log, context.Response.StatusCode, bytes);
    }

    /// <summary>
    /// The words FR-A-16 leaves undefined (SPEC-GAP T-912, Q-65). They describe what became of the request, not what
    /// the caller asked for: a dry run that answered 200 is <c>ok</c>, and what it did is visible in its counts.
    /// </summary>
    private static string OutcomeOf(HttpContext context, int status) =>
        context.Response.Headers.TryGetValue(IdempotencyMiddleware.ReplayedHeader, out var replayed)
        && replayed.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) ? "replayed"
        : status == StatusCodes.Status202Accepted ? "accepted"
        : status is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices ? "ok"
        : status is >= StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError ? "refused"
        : "failed";

    private static void Record(
        HttpContext context, AuditLog audit, IClock clock, ILogger log, int status, byte[]? body)
    {
        if (ApiKeyMiddleware.ClientOf(context) is not { } client)
        {
            return;
        }

        var summary = Summarise(body, context.Response.ContentType);
        var entry = new AuditEntry
        {
            Utc = clock.UtcNow,
            RequestId = RequestId.Of(context) ?? RequestId.None,
            ClientId = client.Id,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            IdempotencyKey = context.Request.Headers.TryGetValue(IdempotencyMiddleware.KeyHeader, out var key)
                ? key.ToString()
                : null,
            Outcome = OutcomeOf(context, status),
            Status = status,
            BatchId = summary.BatchId ?? RouteValue(context, "/batches/"),
            JobId = summary.JobId ?? RouteValue(context, "/jobs/"),
            Counts = summary.Counts,
            TotalAmount = summary.TotalAmount,
        };

        try
        {
            audit.Append(entry);
        }
        catch (IOException ex)
        {
            // The answer has already gone to the caller; losing the line is bad, losing the answer is worse.
            log.LogError(ex, "Audit line could not be written to {Folder}: {Error}", audit.Folder, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.LogError(ex, "Audit line could not be written to {Folder}: {Error}", audit.Folder, ex.Message);
        }
    }

    /// <summary>The <c>{id}</c> of a route such as <c>/jobs/{id}/post</c>, when the path is that shape.</summary>
    private static string? RouteValue(HttpContext context, string segment) =>
        context.Request.Path.HasValue
        && context.Request.Path.Value!.Contains(segment, StringComparison.OrdinalIgnoreCase)
        && context.Request.RouteValues.TryGetValue("id", out var id)
            ? id as string
            : null;

    /// <summary>
    /// The four money fields, read back out of the answer the caller was given. Nothing else is taken from it —
    /// the body holds payees, memos and account names, and none of those belong in this file.
    /// </summary>
    private static (string? BatchId, string? JobId, AuditCounts? Counts, decimal? TotalAmount) Summarise(
        byte[]? body, string? contentType)
    {
        if (body is null or { Length: 0 } or { Length: > MaxBodyToRead }
            || contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return (null, null, null, null);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null);
            }

            return (
                Text(root, "batchId"),
                Text(root, "jobId"),
                CountsOf(root),
                root.TryGetProperty("totals", out var totals)
                && totals.ValueKind == JsonValueKind.Object
                && totals.TryGetProperty("posted", out var posted)
                && posted.TryGetDecimal(out var amount)
                    ? amount
                    : null);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    private static AuditCounts? CountsOf(JsonElement root) =>
        root.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object
            ? new AuditCounts(Number(counts, "submitted"), Number(counts, "posted"), Number(counts, "held"), Number(counts, "skipped"))
            : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
