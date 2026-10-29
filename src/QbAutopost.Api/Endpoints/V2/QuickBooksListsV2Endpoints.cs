using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>Who pressed Sync now (M14 §7.1): the user id and the name the user directory gives it.</summary>
public sealed record ListSyncRequesterView(string Id, string DisplayName);

/// <summary>M14 §7.1: the newest list sync attempt, as the dashboard shows it. Never the raw error.</summary>
public sealed record LastSyncView(
    ListSyncTrigger Trigger,
    ListSyncOutcome Outcome,
    string? Reason,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    ListSyncRequesterView? RequestedBy,
    string Message);

/// <summary>M14 §7.1: <c>GET /api/v2/quickbooks/lists</c>, v1's lists plus <c>lastSync</c> and <c>nextSyncUtc</c>.</summary>
public sealed record QbListsViewV2(
    DateTime? SyncedUtc,
    IReadOnlyList<QbAccount> Accounts,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> Customers,
    LastSyncView? LastSync,
    DateTime? NextSyncUtc);

/// <summary>M14 §7.2: the list sizes a sync found.</summary>
public sealed record ListSyncCountsView(int Accounts, int Vendors, int Customers);

/// <summary>M14 §7.2: the 200 of <c>POST /api/v2/quickbooks/lists/sync</c>: the §7.1 body for the fresh lists, plus counts.</summary>
public sealed record QbListsSyncedViewV2(
    DateTime? SyncedUtc,
    IReadOnlyList<QbAccount> Accounts,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> Customers,
    LastSyncView? LastSync,
    DateTime? NextSyncUtc,
    ListSyncCountsView Counts);

/// <summary>
/// M14 §7 (R-16): the dashboard's QuickBooks lists. <c>GET /quickbooks/lists</c> (<c>lists:read</c>) reads the stored
/// lists and says how old they are and when the next automatic sync is due; <c>POST /quickbooks/lists/sync</c>
/// (<c>requests:edit</c>, or a key with <c>qb:read</c>; the posting rate bucket) is Sync now. v1's routes are untouched.
/// </summary>
public static class QuickBooksListsV2Endpoints
{
    /// <summary>The worker label of a dashboard sync, as v1's sync uses (the log's <c>jobId</c>).</summary>
    public const string SyncLabel = "qb-sync-lists";

    public const string NotOpenType = "quickbooks-not-open";
    public const string TimeoutType = "sync-timeout";

    public static IEndpointRouteBuilder MapQuickBooksListsV2Endpoints(this IEndpointRouteBuilder v2)
    {
        v2.MapGet("/quickbooks/lists", GetListsAsync);
        v2.MapPost(ApiRoutes.ListSyncRoute, SyncAsync);
        return v2;
    }

    private static async Task<IResult> GetListsAsync(
        IQbListsSource lists, ListSyncSchedule schedule, ActorDirectory actors, ILoggerFactory loggers, CancellationToken ct)
    {
        var current = lists.Current;
        var last = await LastSyncAsync(lists.LastAttempt, actors, loggers, ct);
        return Results.Ok(new QbListsViewV2(
            current.SyncedUtc, current.Accounts, current.Vendors, current.Customers, last, schedule.NextSyncUtc));
    }

    /// <summary>
    /// M14 §7.2. Order: the presence guard (never start QuickBooks), then join the sync in flight or queue one on the
    /// worker, then wait at most <c>Api:SyncPostTimeoutSeconds</c>. Not an audit-feed event (Q-121).
    /// </summary>
    private static async Task<IResult> SyncAsync(
        HttpContext http,
        IQbPresence presence,
        QbListSync sync,
        ListSyncSchedule schedule,
        JobQueue queue,
        IQbListsSource lists,
        ActorDirectory actors,
        IOptions<AppSettings> settings,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(QuickBooksListsV2Endpoints));
        var (trigger, requestedBy) = WhoAsks(http);

        if (!IsOpen(presence, log))
        {
            await RecordSkippedAsync(sync, trigger, requestedBy, log, ct);
            log.LogInformation(
                "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome} ({Reason})",
                Lower(trigger), requestedBy ?? "unknown", Lower(ListSyncOutcome.Skipped), ListSyncReasons.QuickBooksNotOpen);
            return NotOpen();
        }

        var (flight, joined) = schedule.JoinOrStart(
            token => RunLoggedAsync(sync, trigger, requestedBy, log, token),
            work => Observe(queue.RunExclusiveAsync(SyncLabel, work, CancellationToken.None)));
        if (joined)
        {
            log.LogInformation("QuickBooks lists sync ({Trigger}, by {RequestedBy}): joined the sync in flight", Lower(trigger), requestedBy ?? "unknown");
        }

        var seconds = settings.Value.Api.SyncPostTimeoutSeconds;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        ListSyncResult? result;
        try
        {
            result = await flight.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.LogInformation("QuickBooks lists sync: still running after {Seconds} s; answering 504, the sync carries on", seconds);
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "The sync is still running",
                detail: $"QuickBooks has not answered within {seconds} s. The sync carries on; GET /api/v2/quickbooks/lists shows its result.",
                type: TimeoutType);
        }
        catch (Exception ex) when (QuickBooksEndpoints.QuickBooksProblem(ex) is { } problem)
        {
            // The same mapping as v1's SyncLists; the failed row was recorded by QbListSync.
            return problem;
        }

        if (result is null)
        {
            // A scheduled attempt this request joined was skipped: QuickBooks closed between the two checks.
            return NotOpen();
        }

        var current = lists.Current;
        var last = await LastSyncAsync(lists.LastAttempt, actors, loggers, ct);
        return Results.Ok(new QbListsSyncedViewV2(
            current.SyncedUtc,
            current.Accounts,
            current.Vendors,
            current.Customers,
            last,
            schedule.NextSyncUtc,
            new ListSyncCountsView(result.Accounts, result.Vendors, result.Customers)));
    }

    /// <summary>
    /// A dashboard user is the <c>dashboard</c> trigger, by their id (§3). An API key on this route is an API caller,
    /// recorded as v1 records it: <c>api</c>, by the client's name.
    /// </summary>
    // SPEC-GAP T-1407 (Q-123): §7.2 names the trigger only for dashboard users; a key here is recorded as `api`.
    private static (ListSyncTrigger Trigger, string? RequestedBy) WhoAsks(HttpContext http)
    {
        var caller = CallerMiddleware.CallerOf(http);
        if (caller is { Kind: CallerKind.User })
        {
            return (ListSyncTrigger.Dashboard, caller.Id);
        }

        var client = CallerMiddleware.ClientOf(http);
        return (ListSyncTrigger.Api, client is null ? caller?.Id : client.Name ?? client.Id);
    }

    /// <summary>§6: never start QuickBooks. A check that itself fails counts as "not open", as for the schedule (Q-122).</summary>
    private static bool IsOpen(IQbPresence presence, ILogger log)
    {
        try
        {
            return presence.IsCompanyOpen();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "QuickBooks presence check failed; the list sync is refused as not open");
            return false;
        }
    }

    private static async Task RecordSkippedAsync(QbListSync sync, ListSyncTrigger trigger, string? requestedBy, ILogger log, CancellationToken ct)
    {
        try
        {
            await sync.RecordSkippedAsync(trigger, requestedBy, ListSyncReasons.QuickBooksNotOpen, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The answer is still 409; the store has logged a database failure.
            log.LogInformation("QuickBooks lists sync: the skipped attempt could not be recorded: {Error}", ex.Message);
        }
    }

    /// <summary>The sync itself, on the worker, with the §12 line for its outcome.</summary>
    private static async Task<ListSyncResult> RunLoggedAsync(
        QbListSync sync, ListSyncTrigger trigger, string? requestedBy, ILogger log, CancellationToken ct)
    {
        try
        {
            var result = await sync.SyncAsync(trigger, requestedBy, ct);
            log.LogInformation(
                "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome}, {Accounts} accounts, {Vendors} vendors, {Customers} customers",
                Lower(trigger), requestedBy ?? "unknown", Lower(ListSyncOutcome.Ok), result.Accounts, result.Vendors, result.Customers);
            if (result.MissingInRules.Count > 0)
            {
                log.LogWarning("Names in rules.json that QuickBooks does not have: {Missing}", string.Join("; ", result.MissingInRules));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(
                "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome}: {Error}",
                Lower(trigger), requestedBy ?? "unknown", Lower(ListSyncOutcome.Failed), ex.Message);
            throw;
        }
    }

    /// <summary>The worker's own copy of the flight's outcome; the flight carries it to the callers.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private static async Task<LastSyncView?> LastSyncAsync(
        ListSyncRun? run, ActorDirectory actors, ILoggerFactory loggers, CancellationToken ct)
    {
        if (run is null)
        {
            return null;
        }

        ListSyncRequesterView? requestedBy = null;
        if (run is { Trigger: ListSyncTrigger.Dashboard, RequestedBy: { Length: > 0 } id })
        {
            requestedBy = new ListSyncRequesterView(id, await DisplayNameAsync(id, actors, loggers, ct));
        }

        return new LastSyncView(
            run.Trigger, run.Outcome, run.Reason, run.StartedUtc, run.FinishedUtc, requestedBy, ListSyncMessages.For(run));
    }

    /// <summary>The user's name, as the audit feed resolves actors; the id when nobody can name it.</summary>
    private static async Task<string> DisplayNameAsync(string id, ActorDirectory actors, ILoggerFactory loggers, CancellationToken ct)
    {
        try
        {
            return (await actors.ResolveAsync([id], ct)).UserOf(id)?.Name ?? id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            loggers.CreateLogger(typeof(QuickBooksListsV2Endpoints))
                .LogWarning("QuickBooks lists: the name of user {UserId} could not be looked up: {Error}", id, ex.Message);
            return id;
        }
    }

    private static IResult NotOpen() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "QuickBooks isn't open",
            detail: "Open QuickBooks on the company file, then sync again.",
            type: NotOpenType);

    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
