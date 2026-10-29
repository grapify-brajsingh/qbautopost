using QbAutopost.Api.Database;
using QbAutopost.Api.Health;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Endpoints;

/// <summary>Health routes (spec §6, FR-16). No API key needed; 503 when the dependency is not ok.</summary>
public static class HealthEndpoints
{
    /// <param name="flavor">T-1109: <see cref="ApiFlavor.V2"/> maps <c>/health/database</c> only (dashboard v2 spec 3); T-1212b adds <c>/health</c>.</param>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app, ApiFlavor flavor = ApiFlavor.V1)
    {
        var health = app.MapGroup("/health");
        if (flavor == ApiFlavor.V2)
        {
            // T-1212b (dashboard-api-m12 8, 14.1): the v1 body plus model and extraction, for a signed-in user or a monitor.
            health.MapGet("/", (AppHealth h) => Results.Ok(h.NowV2()));
            health.MapGet("/database", GetDatabase);
            return app;
        }

        health.MapGet("/", GetApp);
        health.MapGet("/ready", GetReady);
        health.MapGet("/sdk", GetSdk);
        health.MapGet("/hermes", GetHermes);
        health.MapGet("/quickbooks", GetQuickBooks);
        health.MapGet("/database", GetDatabase);
        return app;
    }

    /// <summary>
    /// Saved-requests spec 5.5. Looks at the database again once the last check is older than
    /// <see cref="DatabaseGate.StaleAfter"/> (M10 review: the shared, throttled refresh R13 gave the filter, so a
    /// monitor polling this costs one check per 10 s, not one per poll) and reports reachability, schema state and
    /// latency. The body is built from the gate's state and migration names only, so the connection string cannot
    /// reach it.
    /// </summary>
    private static async Task<IResult> GetDatabase(DatabaseGate gate, CancellationToken ct)
    {
        if (gate.IsStale)
        {
            await gate.RefreshSharedAsync(ct);
        }

        var view = new DatabaseHealthView(
            gate.IsReady,
            gate.State.ToString(),
            gate.Reason,
            gate.State is DatabaseState.Ready or DatabaseState.SchemaOutOfDate,
            gate.IsReady && gate.Pending.Count == 0,
            gate.Pending,
            gate.LatencyMs);
        return view.Ok
            ? Results.Ok(view)
            : Results.Json(view, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// FR-A-1. In-process state only: it never calls QuickBooks or Hermes, so it answers in milliseconds even while a
    /// post holds the gateway — the difference between "the app is dead" and "the app is busy".
    /// </summary>
    private static IResult GetApp(AppHealth health) => Results.Ok(health.Now());

    /// <summary>
    /// FR-A-3. The SDK itself: registration, bitness, configured qbXML version. No company file is opened and no
    /// session is begun, so it answers while QuickBooks is closed — and a bitness mismatch (the T-609 failure) is
    /// reported as not ok rather than as a warning.
    /// </summary>
    private static async Task<IResult> GetSdk(IQbSdkProbe probe, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(HealthEndpoints));
        var info = await probe.ProbeAsync(ct);
        if (info.Ok)
        {
            log.LogDebug("QuickBooks SDK health: ok, {Message}", info.Message);
            return Results.Ok(info);
        }

        log.LogWarning("QuickBooks SDK health: not ok: {Message}", info.Message);
        return Results.Json(info, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// FR-A-2. 503 when an <c>error</c> check failed; the body lists every check either way. M10 review: a gate that
    /// is not Ready is looked at again first, throttled and shared as the filter does (R13), so an outage that has
    /// ended stops being reported here without anyone calling <c>/health/database</c>.
    /// </summary>
    private static async Task<IResult> GetReady(AppHealth health, DatabaseGate gate, CancellationToken ct)
    {
        if (gate.NeedsLookAgain)
        {
            await gate.RefreshSharedAsync(ct);
        }

        var view = health.Ready();
        return view.Ok
            ? Results.Ok(view)
            : Results.Json(view, statusCode: StatusCodes.Status503ServiceUnavailable);
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
