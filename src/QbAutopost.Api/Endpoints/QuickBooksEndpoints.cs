using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Api.Endpoints;

/// <summary>QuickBooks routes that are not about one job (spec §6): <c>POST /qb/sync-lists</c>.</summary>
public static class QuickBooksEndpoints
{
    /// <summary>FR-A-4 request; every field optional.</summary>
    public sealed record ConnectionTestRequest(string? CompanyFile, int? TimeoutSeconds, bool? IncludeCompanyInfo);

    public static IEndpointRouteBuilder MapQuickBooksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/qb/sync-lists", SyncLists);
        app.MapPost("/quickbooks/connection/test", ConnectionTest);
        return app;
    }

    /// <summary>
    /// FR-A-4. Not queued behind jobs, but it still takes the QuickBooks lock, so the wait is reported as its own
    /// step: an operator can then tell "QuickBooks is slow" from "another call was ahead of me".
    /// </summary>
    private static async Task<IResult> ConnectionTest(
        ConnectionTestRequest? request,
        IOptions<AppSettings> settings,
        QbConnectionCheck check,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(QuickBooksEndpoints));
        var s = settings.Value;

        // Both refusals happen before any gateway call: a request we will not honour must not touch QuickBooks.
        if (!string.IsNullOrWhiteSpace(request?.CompanyFile) && !s.QuickBooks.AllowCompanyFileOverride)
        {
            log.LogWarning("Connection test refused: a company file was given but QuickBooks:AllowCompanyFileOverride is false");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Company file override is not allowed",
                detail: "This installation serves one company. Set QuickBooks:AllowCompanyFileOverride to name a company file per request.");
        }

        var max = s.Api.MaxConnectionTestTimeoutSeconds;
        var seconds = request?.TimeoutSeconds ?? s.QuickBooks.ConnectionTestTimeoutSeconds;
        if (seconds <= 0 || seconds > max)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid timeout",
                detail: $"timeoutSeconds must be between 1 and {max}.");
        }

        var result = await check.RunAsync(TimeSpan.FromSeconds(seconds), request?.IncludeCompanyInfo ?? true, ct);
        foreach (var step in result.Steps.Where(step => step.Message is not null))
        {
            log.LogInformation("Connection test step {Step}: {Ms} ms, {Message}", step.Name, step.Ms, step.Message);
        }

        if (result.Ok)
        {
            log.LogInformation(
                "Connection test: ok in {TotalMs} ms, {Product}, company file {CompanyFile}", result.TotalMs, result.Product, result.CompanyFile);
            return Results.Ok(result);
        }

        log.LogWarning("Connection test: not ok after {TotalMs} ms: {Message}", result.TotalMs, result.Message);
        return Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
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
