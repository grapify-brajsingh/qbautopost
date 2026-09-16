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
    private static async Task<IResult> GetQuickBooks(QbHealth health, CancellationToken ct)
    {
        var result = await health.CheckAsync(ct);
        return result.Ok
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> GetHermes(IHermesClient hermes, CancellationToken ct)
    {
        var ping = await hermes.PingAsync(ct);
        return ping.Ok
            ? Results.Ok(ping)
            : Results.Json(ping, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
