using System.Globalization;
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
/// M15 §3 (R-17): one recorded attempt in <c>GET /api/v2/quickbooks/lists/syncs</c>: the <see cref="LastSyncView"/> fields
/// (one shared mapping), plus the id, the counts of an Ok attempt (else null) and whether its lists are the ones in use.
/// Never the error, the names missing in the rules or the stored lists.
/// </summary>
public sealed record ListSyncHistoryItemView(
    long Id,
    ListSyncTrigger Trigger,
    ListSyncOutcome Outcome,
    string? Reason,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    ListSyncRequesterView? RequestedBy,
    string Message,
    ListSyncCountsView? Counts,
    bool Current);

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
    public const string StoppingType = "api-stopping";

    public const int DefaultHistoryPageSize = 25;
    public const int MaxHistoryPageSize = 100;

    private static readonly string[] HistoryParameters = ["trigger", "outcome", "page", "pageSize"];

    public static IEndpointRouteBuilder MapQuickBooksListsV2Endpoints(this IEndpointRouteBuilder v2)
    {
        v2.MapGet("/quickbooks/lists", GetListsAsync);
        v2.MapPost(ApiRoutes.ListSyncRoute, SyncAsync);
        v2.MapGet(ApiRoutes.ListSyncHistoryRoute, HistoryAsync);
        return v2;
    }

    /// <summary>
    /// M15 §3 (R-17): every recorded attempt, newest first, filtered and paged. Each item reads as <c>lastSync</c> does.
    /// In file mode the store answers from the attempts since the API started (Q-125).
    /// </summary>
    private static async Task<IResult> HistoryAsync(
        HttpContext http, IQbListsStore store, ActorDirectory actors, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!TryParseHistory(http.Request.Query, out var query, out var error))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid query", detail: error, type: "invalid-request");
        }

        var page = await store.ListAttemptsAsync(query!, ct);
        var nameOf = await RequesterNamesAsync(page.Items.Select(a => a.Run), actors, loggers, ct);
        var items = page.Items.Select(a => HistoryItemOf(a, page.CurrentId, nameOf)).ToList();
        return Results.Ok(new PageView<ListSyncHistoryItemView>(items, query!.Page, query.PageSize, page.Total));
    }

    private static ListSyncHistoryItemView HistoryItemOf(ListSyncAttempt attempt, long? currentId, Func<string, string> nameOf)
    {
        var view = ViewOf(attempt.Run, nameOf);
        var counts = attempt.Run is { Outcome: ListSyncOutcome.Ok, Accounts: { } accounts, Vendors: { } vendors, Customers: { } customers }
            ? new ListSyncCountsView(accounts, vendors, customers)
            : null;
        return new ListSyncHistoryItemView(
            attempt.Id, view.Trigger, view.Outcome, view.Reason, view.StartedUtc, view.FinishedUtc, view.RequestedBy, view.Message,
            counts, attempt.Id == currentId);
    }

    /// <summary>
    /// M15 §3: <c>trigger</c> and <c>outcome</c> are comma-separated sets of names (ignoring case), <c>page</c> is 1 or more,
    /// <c>pageSize</c> 1 to 100. An unknown parameter or value, a parameter given twice or an empty entry is refused.
    /// </summary>
    private static bool TryParseHistory(IQueryCollection q, out ListSyncQuery? query, out string? error)
    {
        query = null;
        error = null;
        foreach (var (name, values) in q)
        {
            if (!HistoryParameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Unknown parameter '{name}'. The parameters are {string.Join(", ", HistoryParameters)}.";
                return false;
            }

            if (values.Count > 1)
            {
                error = $"{name} may be given once (a set is comma-separated).";
                return false;
            }
        }

        if (!TrySet<ListSyncTrigger>(q, "trigger", out var triggers) || !TrySet<ListSyncOutcome>(q, "outcome", out var outcomes))
        {
            error = $"trigger is a comma-separated set of {Names<ListSyncTrigger>()}; outcome of {Names<ListSyncOutcome>()}.";
            return false;
        }

        var page = 1;
        if (q.TryGetValue("page", out var pageText)
            && (!int.TryParse(pageText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            error = "page must be 1 or more.";
            return false;
        }

        var pageSize = DefaultHistoryPageSize;
        if (q.TryGetValue("pageSize", out var sizeText)
            && (!int.TryParse(sizeText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1 || pageSize > MaxHistoryPageSize))
        {
            error = $"pageSize must be between 1 and {MaxHistoryPageSize}.";
            return false;
        }

        query = new ListSyncQuery(triggers, outcomes, page, pageSize);
        return true;
    }

    /// <summary>Absent is "any" (null). Names only, ignoring case: never a number, never an empty entry.</summary>
    private static bool TrySet<T>(IQueryCollection q, string name, out IReadOnlySet<T>? set)
        where T : struct, Enum
    {
        set = null;
        if (!q.TryGetValue(name, out var text))
        {
            return true;
        }

        var parsed = new HashSet<T>();
        foreach (var entry in text.ToString().Split(','))
        {
            var match = Enum.GetValues<T>().Where(v => Lower(v).Equals(entry.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count != 1)
            {
                return false;
            }

            parsed.Add(match[0]);
        }

        set = parsed;
        return true;
    }

    private static string Names<T>()
        where T : struct, Enum => string.Join("|", Enum.GetValues<T>().Select(Lower));

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
        ListSyncCompletion? done;
        try
        {
            done = await flight.WaitAsync(wait.Token);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The flight itself was cancelled: the host is stopping (the worker's token), not this caller's wait.
            log.LogInformation("QuickBooks lists sync: cancelled because the API is stopping; answering 503");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The API is stopping",
                detail: "The API is stopping; try again in a moment.",
                type: StoppingType);
        }
        catch (Exception ex) when (SyncProblem(ex) is { } problem)
        {
            // The failed row was recorded by QbListSync, and the raw error logged where the sync ran.
            return problem;
        }

        if (done is null)
        {
            // A scheduled attempt this request joined was skipped: QuickBooks closed between the two checks.
            return NotOpen();
        }

        // The run and lists this flight produced, not whatever the source holds by now (§7.2: lastSync = this run).
        var last = await LastSyncAsync(done.Run, actors, loggers, ct);
        return Results.Ok(new QbListsSyncedViewV2(
            done.Lists.SyncedUtc,
            done.Lists.Accounts,
            done.Lists.Vendors,
            done.Lists.Customers,
            last,
            schedule.NextSyncUtc,
            new ListSyncCountsView(done.Result.Accounts, done.Result.Vendors, done.Result.Customers)));
    }

    /// <summary>
    /// M14 §7.2: v1's QuickBooks mapping (<see cref="QuickBooksEndpoints.QuickBooksProblem"/> decides which failures are
    /// QuickBooks's, and its status and title), with the v2 code as the problem <c>type</c> (the reason
    /// <see cref="QbListSync.ReasonOf"/> records) and a fixed detail: never the exception text, which stays in the log
    /// and the recorded row. Null for any other failure.
    /// </summary>
    public static IResult? SyncProblem(Exception ex)
    {
        if (QuickBooksEndpoints.QuickBooksProblem(ex) is null)
        {
            return null;
        }

        return QbListSync.ReasonOf(ex) switch
        {
            ListSyncReasons.QuickBooksBusy => Problem(
                StatusCodes.Status503ServiceUnavailable, UnavailableTitle, ListSyncReasons.QuickBooksBusy,
                "QuickBooks did not answer in time; it may be showing a dialog. The lists from the last good sync are still used."),
            ListSyncReasons.QbStatus => Problem(
                StatusCodes.Status502BadGateway, RefusedTitle, ListSyncReasons.QbStatus,
                "QuickBooks refused the list query or sent an answer that could not be read. The lists from the last good sync are still used."),
            _ => Problem(
                StatusCodes.Status503ServiceUnavailable, UnavailableTitle, ListSyncReasons.QuickBooksUnavailable,
                "QuickBooks could not be reached. The lists from the last good sync are still used; try again in a moment."),
        };
    }

    private const string UnavailableTitle = "QuickBooks is not available";
    private const string RefusedTitle = "QuickBooks refused the request";

    private static IResult Problem(int status, string title, string type, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, type: type);

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
    private static async Task<ListSyncCompletion> RunLoggedAsync(
        QbListSync sync, ListSyncTrigger trigger, string? requestedBy, ILogger log, CancellationToken ct)
    {
        try
        {
            var completion = await sync.SyncWithRunAsync(trigger, requestedBy, ct);
            var result = completion.Result;
            log.LogInformation(
                "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome}, {Accounts} accounts, {Vendors} vendors, {Customers} customers",
                Lower(trigger), requestedBy ?? "unknown", Lower(ListSyncOutcome.Ok), result.Accounts, result.Vendors, result.Customers);
            if (result.MissingInRules.Count > 0)
            {
                log.LogWarning("Names in rules.json that QuickBooks does not have: {Missing}", string.Join("; ", result.MissingInRules));
            }

            return completion;
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
        ListSyncRun? run, ActorDirectory actors, ILoggerFactory loggers, CancellationToken ct) =>
        run is null ? null : ViewOf(run, await RequesterNamesAsync([run], actors, loggers, ct));

    /// <summary>
    /// The one mapping of a run to what the dashboard shows (M14 §7.1, M15 §3): <c>lastSync</c> and every history item.
    /// <c>requestedBy</c> only for a dashboard sync; <c>message</c> by outcome and reason, never the raw error.
    /// </summary>
    private static LastSyncView ViewOf(ListSyncRun run, Func<string, string> nameOf)
    {
        var id = RequesterOf(run);
        var requestedBy = id is null ? null : new ListSyncRequesterView(id, nameOf(id));
        return new LastSyncView(
            run.Trigger, run.Outcome, run.Reason, run.StartedUtc, run.FinishedUtc, requestedBy, ListSyncMessages.For(run));
    }

    private static string? RequesterOf(ListSyncRun run) =>
        run is { Trigger: ListSyncTrigger.Dashboard, RequestedBy: { Length: > 0 } id } ? id : null;

    /// <summary>
    /// The names of the users who pressed Sync now in <paramref name="runs"/>, in one lookup, as the audit feed resolves
    /// actors; the id when nobody can name it or the lookup fails.
    /// </summary>
    private static async Task<Func<string, string>> RequesterNamesAsync(
        IEnumerable<ListSyncRun> runs, ActorDirectory actors, ILoggerFactory loggers, CancellationToken ct)
    {
        var ids = runs.Select(RequesterOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return id => id;
        }

        try
        {
            var names = await actors.ResolveAsync(ids, ct);
            return id => names.UserOf(id)?.Name ?? id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            loggers.CreateLogger(typeof(QuickBooksListsV2Endpoints))
                .LogWarning("QuickBooks lists: the name of user {UserId} could not be looked up: {Error}", string.Join(", ", ids), ex.Message);
            return id => id;
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
