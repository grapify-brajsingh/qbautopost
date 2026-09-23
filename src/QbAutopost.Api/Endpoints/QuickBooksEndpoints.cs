using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Endpoints;

/// <summary>QuickBooks routes that are not about one job (spec §6): <c>POST /qb/sync-lists</c>.</summary>
public static class QuickBooksEndpoints
{
    /// <summary>FR-A-5 request; both fields optional.</summary>
    public sealed record CompanyFileRequest(string? CompanyFile, bool? RequireBackup);

    /// <summary>FR-A-4 request; every field optional.</summary>
    public sealed record ConnectionTestRequest(string? CompanyFile, int? TimeoutSeconds, bool? IncludeCompanyInfo);

    public static IEndpointRouteBuilder MapQuickBooksEndpoints(this IEndpointRouteBuilder app)
    {
        // T-914 (api-v1 §3): the versioned spelling of the sync, and the read of what it cached. The flat
        // /qb/sync-lists stays mapped while Api:LegacyRoutes is true (api-v1 §9.1) so no caller is broken by the
        // rename; the documentation now names the route on the right — see Q-69.
        app.MapPost("/qb/sync-lists", SyncLists);
        app.MapPost("/quickbooks/lists/sync", SyncLists);
        app.MapGet("/quickbooks/lists", GetLists);
        app.MapPost("/quickbooks/connection/test", ConnectionTest);
        app.MapPost("/quickbooks/company-file/validate", ValidateCompanyFile);
        app.MapPost("/quickbooks/transactions/validate", ValidateTransactions);
        app.MapPost("/quickbooks/transactions", PostTransactions);
        return app;
    }

    /// <summary>
    /// FR-A-8, FR-A-10. The post. It runs on the single worker (<see cref="JobQueue.RunExclusiveAsync"/>), so it can
    /// never overlap a folder job, and the caller waits at most <c>Api:SyncPostTimeoutSeconds</c> for it; past that
    /// they get 202 and a batch to poll, while the work carries on.
    /// <para>
    /// The batch is on disk before it is queued (rule 7). A dry run ends in <c>ready</c> without a single byte sent.
    /// </para>
    /// </summary>
    private static async Task<IResult> PostTransactions(
        DirectRequest? request,
        HttpContext http,
        IOptions<AppSettings> settings,
        JobQueue queue,
        DirectPostRunner runner,
        ApiBatchStore batches,
        ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger(typeof(QuickBooksEndpoints));
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A request body is required",
                detail: "Send the transactions to post as JSON (api-v1 §6.1).");
        }

        var plan = runner.Plan(request);
        if (plan.Errors.Count > 0)
        {
            // Refused before it has an identity: no batch id, no folder, nothing to undo later.
            log.LogWarning(
                "Post refused {Rows} row(s) for reference {Reference}: {Errors}",
                plan.Submitted, request.Reference, string.Join("; ", plan.Errors));
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The request could not be read",
                detail: string.Join("; ", plan.Errors),
                extensions: new Dictionary<string, object?> { ["errors"] = plan.Errors });
        }

        var s = settings.Value;
        var dryRun = request.DryRun ?? s.DryRunDefault;
        var batchId = runner.NextBatchId(request);
        var opening = runner.Begin(batchId, request, plan, dryRun);
        if (dryRun)
        {
            return Results.Ok(opening);
        }

        log.LogInformation("Batch {BatchId}: queued for posting, {Rows} row(s) to post", batchId, plan.WouldPost);

        // The work runs to the end whatever the caller does; this token only bounds the wait (FR-A-10).
        var async = http.Request.Headers.TryGetValue("Prefer", out var prefer)
                    && prefer.Any(p => p?.Contains("respond-async", StringComparison.OrdinalIgnoreCase) == true);
        using var wait = new CancellationTokenSource(async ? TimeSpan.Zero : TimeSpan.FromSeconds(s.Api.SyncPostTimeoutSeconds));
        try
        {
            var result = await queue.RunExclusiveAsync(
                batchId, ct => runner.RunAsync(batchId, request, plan, ct), wait.Token);
            return result.Unavailable
                ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(result);
        }
        catch (OperationCanceledException)
        {
            // Still running. The batch is already persisted, so the caller has somewhere to look.
            log.LogInformation("Batch {BatchId}: still posting after the synchronous budget; answering 202", batchId);
            var location = ApiRoutes.V1Prefix + "/batches/" + batchId;
            var current = batches.Load(batchId) ?? opening;
            return Results.Accepted(location, new { current.BatchId, current.Status, location });
        }
    }

    /// <summary>
    /// FR-A-6. The offline dry run: the same reader, mapper, gates and qbXML builder the post uses, stopping short of
    /// the SDK call. Nothing here may reach QuickBooks or Hermes — that is what lets a caller validate a batch while
    /// the QuickBooks server is still shut down.
    /// <para>
    /// 400 when the request contradicts itself (a control total that does not add up, no rows, too many); 422 when it
    /// was understood and a gate refused a row; 200 when it would post clean. The body is the same either way.
    /// </para>
    /// </summary>
    private static IResult ValidateTransactions(
        DirectRequest? request,
        bool? includeQbXml,
        HttpContext http,
        PipelineOptions pipeline,
        DirectPlanner planner,
        IClock clock,
        ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger(typeof(QuickBooksEndpoints));
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A request body is required",
                detail: "Send the transactions to validate as JSON (api-v1 §6.1).");
        }

        var plan = planner.Plan(
            request,
            Rules.Load(pipeline.RulesFile),
            new QbListsStore(pipeline.QbListsFile).Load(),
            new LedgerStore(pipeline.LedgerFile).Load(),
            DateOnly.FromDateTime(clock.UtcNow),
            pipeline.QbXmlVersion);

        if (plan.Errors.Count > 0)
        {
            log.LogWarning(
                "Transaction validation refused {Rows} row(s) for reference {Reference}: {Errors}",
                plan.Submitted, request.Reference, string.Join("; ", plan.Errors));
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The request could not be read",
                detail: string.Join("; ", plan.Errors),
                extensions: new Dictionary<string, object?> { ["errors"] = plan.Errors });
        }

        // T-910: the qbXML body carries every amount and name in the batch, so it needs the qb:debug scope (FR-A-6).
        var response = ValidationResponse.From(
            plan,
            includeQbXml == true && ApiKeyMiddleware.HasScope(http, ApiScopes.QbDebug),
            asked: includeQbXml == true);
        log.LogInformation(
            "Validated {Submitted} row(s) for reference {Reference}: {WouldPost} would post, {Held} held, {Duplicates} duplicate(s)",
            plan.Submitted, request.Reference, plan.WouldPost, plan.Held, plan.Duplicates);

        return plan.Ok
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>
    /// FR-A-5. Read-only: no qbXML is sent, only "which file do you have open?". 422 when an error check failed, so a
    /// caller can act on the status alone, and the body lists every check either way.
    /// </summary>
    /// <summary>
    /// T-911 (FR-A-17): an overridden company file must sit inside <c>QuickBooks:AllowedCompanyFolders</c>. An empty
    /// list means unrestricted, which is safe here only because <c>AllowCompanyFileOverride</c> is false by default —
    /// a caller cannot name a file at all until the owner has turned the override on.
    /// </summary>
    private static IResult? OutsideAllowedFolders(string? companyFile, AppSettings s, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(companyFile)
            || PathAllowList.IsInside(companyFile, [.. s.QuickBooks.AllowedCompanyFolders]))
        {
            return null;
        }

        log.LogWarning("Request refused: the company file named is outside QuickBooks:AllowedCompanyFolders");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Company file is not in an allowed folder",
            detail: "The company file must be an absolute path inside QuickBooks:AllowedCompanyFolders.");
    }

    private static async Task<IResult> ValidateCompanyFile(
        CompanyFileRequest? request,
        IOptions<AppSettings> settings,
        CompanyFileValidator validator,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(QuickBooksEndpoints));
        var s = settings.Value;
        if (!string.IsNullOrWhiteSpace(request?.CompanyFile) && !s.QuickBooks.AllowCompanyFileOverride)
        {
            log.LogWarning("Company file validation refused: a company file was given but QuickBooks:AllowCompanyFileOverride is false");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Company file override is not allowed",
                detail: "This installation serves one company. Set QuickBooks:AllowCompanyFileOverride to name a company file per request.");
        }

        if (OutsideAllowedFolders(request?.CompanyFile, s, log) is { } outside)
        {
            return outside;
        }

        var companyFile = string.IsNullOrWhiteSpace(request?.CompanyFile) ? s.Company.FilePath : request.CompanyFile;
        CompanyFileValidation result;
        try
        {
            result = await validator.ValidateAsync(companyFile, ct);
        }
        catch (Exception ex) when (QuickBooksProblem(ex) is { } problem)
        {
            log.LogWarning("Company file validation could not reach QuickBooks: {Error}", ex.Message);
            return problem;
        }

        foreach (var check in result.Checks.Where(c => !c.Ok))
        {
            log.LogWarning("Company file check {Check} failed ({Severity}): {Message}", check.Name, check.Severity, check.Message);
        }

        if (result.Ok)
        {
            log.LogInformation("Company file validated: {CompanyFile}, {Message}", result.CompanyFile, result.Message);
            return Results.Ok(result);
        }

        return Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
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

        if (OutsideAllowedFolders(request?.CompanyFile, s, log) is { } outside)
        {
            return outside;
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

    /// <summary>
    /// T-914 (api-v1 §3): the cached QuickBooks names, so a caller can pick an account or a vendor that exists
    /// instead of guessing and having the row held.
    /// <para>
    /// It reads <c>qb-lists.json</c> and never opens a QuickBooks session: filling a drop-down must not queue
    /// behind a post, and must still answer while QuickBooks is shut down. A file that is not there is not an
    /// error — it means the sync has not run, which the empty lists and the null <c>syncedUtc</c> say plainly.
    /// </para>
    /// </summary>
    private static IResult GetLists(PipelineOptions pipeline) =>
        Results.Ok(new QbListsStore(pipeline.QbListsFile).Load());

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
