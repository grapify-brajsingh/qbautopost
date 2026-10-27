using System.Globalization;

namespace QbAutopost.Api.Database;

/// <summary>
/// Spec 10.7: every saved-request route answers <c>503 database-unavailable</c> with <c>Retry-After: 30</c> while the
/// gate is not ready, before any store call. A store call that fails with a database error makes the gate look again
/// (no timer: the failure is the trigger) and answers the same 503.
/// </summary>
public static class DatabaseGateFilter
{
    public const int RetryAfterSeconds = 30;
    public const string Title = "Database unavailable";

    public static RouteGroupBuilder RequireDatabase(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var gate = context.HttpContext.RequestServices.GetRequiredService<DatabaseGate>();
            if (!gate.IsReady)
            {
                return Unavailable(context.HttpContext, gate);
            }

            try
            {
                return await next(context);
            }
            catch (Exception ex) when (DatabaseGate.IsDatabaseFailure(ex))
            {
                await gate.RefreshAsync(context.HttpContext.RequestAborted);
                return Unavailable(context.HttpContext, gate);
            }
        });
        return group;
    }

    private static IResult Unavailable(HttpContext http, DatabaseGate gate)
    {
        http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: Title,
            type: "database-unavailable",
            detail: $"The saved-requests database is not available ({gate.Reason ?? "unreachable"}). Retry shortly; other routes are unaffected.");
    }
}
