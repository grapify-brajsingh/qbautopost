using System.Text;
using System.Text.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-12: <c>Idempotency-Key</c> on the mutating routes. A caller whose connection dropped can send the same
/// request again and get their first answer back, instead of posting the money twice.
/// <para>
/// It sits behind <see cref="ApiKeyMiddleware"/> — an unauthenticated request must not be able to claim a key, or to
/// learn from a 409 which keys another caller has used.
/// </para>
/// <para>
/// A key is released again whenever the request produced no 2xx: nothing was accepted, so a caller who corrects a
/// typo and resends with the same key should be served rather than locked out. The protection against an actual
/// double posting does not rest here — it rests on G4 and the ledger, which see a repeat with or without a key.
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";
    public const int MaxKeyLength = 128;

    /// <summary>Seconds a caller is asked to wait when their earlier identical request is still running.</summary>
    private const int RetryAfterSeconds = 5;

    public async Task InvokeAsync(
        HttpContext context,
        IdempotencyStore store,
        IClock clock,
        IProblemDetailsService problems,
        ILogger<IdempotencyMiddleware> log)
    {
        if (!ApiRoutes.IsIdempotent(context.Request.Method, context.Request.Path)
            || !context.Request.Headers.TryGetValue(KeyHeader, out var header))
        {
            await next(context);
            return;
        }

        var key = header.ToString();
        var route = $"{context.Request.Method} {context.Request.Path}";
        if (key.Length is 0 or > MaxKeyLength)
        {
            await Problem(
                context, problems, StatusCodes.Status400BadRequest, "Invalid Idempotency-Key",
                $"idempotency-key-invalid: the key must be 1 to {MaxKeyLength} characters.");
            return;
        }

        // Buffered so the body can be hashed here and still be read by model binding afterwards.
        context.Request.EnableBuffering();
        var body = await ReadBodyAsync(context.Request);
        var claim = store.Claim(key, route, IdempotencyStore.HashOf(body), clientId: null, clock.UtcNow);

        switch (claim.Verdict)
        {
            case IdempotencyVerdict.Replay:
                log.LogInformation("Idempotency-Key replayed on {Route}; nothing was sent to QuickBooks", route);
                await ReplayAsync(context, claim.Entry!);
                return;

            case IdempotencyVerdict.InProgress:
                log.LogInformation("Idempotency-Key is still running on {Route}; asking the caller to wait", route);
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                await Problem(
                    context, problems, StatusCodes.Status409Conflict, "The first request is still running",
                    $"idempotency-in-progress: an earlier request with this key has not finished; retry in {RetryAfterSeconds} seconds.");
                return;

            case IdempotencyVerdict.Conflict:
                log.LogWarning("Idempotency-Key reused on {Route} for a different body; refused", route);
                await Problem(
                    context, problems, StatusCodes.Status409Conflict, "Idempotency-Key already used",
                    "idempotency-key-reused: this key was used for a different request body.");
                return;

            default:
                await RunAndRecordAsync(context, store, clock, key, route);
                return;
        }
    }

    /// <summary>Runs the request with its response captured, so the answer can be replayed verbatim later.</summary>
    private async Task RunAndRecordAsync(HttpContext context, IdempotencyStore store, IClock clock, string key, string route)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            // The request blew up, so nothing can be claimed about what it did; free the key and let it surface.
            context.Response.Body = original;
            store.Release(key, route);
            throw;
        }

        context.Response.Body = original;
        var bytes = buffer.ToArray();
        await original.WriteAsync(bytes, context.RequestAborted);

        var status = context.Response.StatusCode;
        if (status is < StatusCodes.Status200OK or >= StatusCodes.Status300MultipleChoices)
        {
            // Refused or failed: nothing was accepted, so the key goes back for the caller to use on their next try.
            store.Release(key, route);
            return;
        }

        var text = Encoding.UTF8.GetString(bytes);
        store.Complete(key, route, status, text, context.Response.ContentType, BatchIdOf(text), clock.UtcNow);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyEntry entry)
    {
        context.Response.StatusCode = entry.StatusCode;
        context.Response.Headers[ReplayedHeader] = "true";
        if (entry.ContentType is { Length: > 0 } contentType)
        {
            context.Response.ContentType = contentType;
        }

        await context.Response.WriteAsync(entry.Response ?? string.Empty, context.RequestAborted);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        request.Body.Position = 0;
        return buffer.ToArray();
    }

    /// <summary>FR-A-12 records the batch a key produced, so an operator can trace a replayed answer to a posting.</summary>
    private static string? BatchIdOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("batchId", out var id)
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task Problem(
        HttpContext context, IProblemDetailsService problems, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        return problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        }).AsTask();
    }
}
