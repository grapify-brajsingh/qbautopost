using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Endpoints;

/// <summary>Health routes (spec §6, FR-16). No API key needed; 503 when the dependency is not ok.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var health = app.MapGroup("/health");
        health.MapGet("/hermes", GetHermes);
        // TODO(T-607): /health/quickbooks
        return app;
    }

    private static async Task<IResult> GetHermes(IHermesClient hermes, CancellationToken ct)
    {
        var ping = await hermes.PingAsync(ct);
        return ping.Ok
            ? Results.Ok(ping)
            : Results.Json(ping, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
