using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Endpoints;

/// <summary>Health routes (spec §6, FR-16). No API key needed; 503 when the dependency is not ok.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var health = app.MapGroup("/health");
        health.MapGet("/hermes", GetHermes);
        health.MapGet("/quickbooks", GetQuickBooks);
        return app;
    }

    /// <summary>
    /// FR-16. Not queued behind jobs (a health check must answer while a job analyses); the gateway still keeps QuickBooks
    /// calls one at a time, so during a post this waits for it (at most the busy timeout).
    /// </summary>
    private static async Task<IResult> GetQuickBooks(QbHealth health, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(HealthEndpoints));
        var result = await health.CheckAsync(ct);
        if (result.Ok)
        {
            log.LogInformation("QuickBooks health: ok, company file {CompanyFile}, {Message}", result.CompanyFile, result.Message);
            return Results.Ok(result);
        }

        log.LogWarning("QuickBooks health: not ok: {Message}", result.Message);
        return Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> GetHermes(IHermesClient hermes, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(HealthEndpoints));
        var ping = await hermes.PingAsync(ct);
        if (ping.Ok)
        {
            log.LogDebug("Hermes health: ok, model {Model}, {LatencyMs} ms", ping.Model, ping.LatencyMs);
            return Results.Ok(ping);
        }

        log.LogWarning("Hermes health: not ok after {LatencyMs} ms (model {Model}): {Message}", ping.LatencyMs, ping.Model, ping.Message);
        return Results.Json(ping, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
