using QbAutopost.Api.Jobs;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Api.Endpoints;

/// <summary>QuickBooks routes that are not about one job (spec §6): <c>POST /qb/sync-lists</c>.</summary>
public static class QuickBooksEndpoints
{
    public static IEndpointRouteBuilder MapQuickBooksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/qb/sync-lists", SyncLists);
        return app;
    }

    /// <summary>
    /// Maps a QuickBooks failure to problem details: 503 when QuickBooks could not be reached or did not answer in time,
    /// 502 when it answered with an error or an unreadable response. Anything else is not handled here.
    /// </summary>
    public static IResult? QuickBooksProblem(Exception ex) => ex switch
    {
        QuickBooksUnavailableException or QuickBooksBusyException or QuickBooksCallException =>
            Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "QuickBooks is not available", detail: ex.Message),
        QbStatusException or FormatException or System.Xml.XmlException =>
            Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "QuickBooks refused the request", detail: ex.Message),
        _ => null,
    };

    private static async Task<IResult> SyncLists(JobQueue queue, QbListSync sync, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(QuickBooksEndpoints));
        try
        {
            // Through the worker: the sync must not overlap a job that reads qb-lists.json or talks to QuickBooks.
            var result = await queue.RunExclusiveAsync("qb-sync-lists", sync.SyncAsync, ct);
            log.LogInformation(
                "QuickBooks lists synced: {Accounts} accounts, {Vendors} vendors, {Customers} customers",
                result.Accounts, result.Vendors, result.Customers);
            if (result.MissingInRules.Count > 0)
            {
                log.LogWarning("Names in rules.json that QuickBooks does not have: {Missing}", string.Join("; ", result.MissingInRules));
            }

            return Results.Ok(result);
        }
        catch (Exception ex) when (QuickBooksProblem(ex) is { } problem)
        {
            log.LogWarning("QuickBooks list sync failed, qb-lists.json unchanged: {Error}", ex.Message);
            return problem;
        }
    }
}
