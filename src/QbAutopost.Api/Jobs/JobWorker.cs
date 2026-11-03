using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// The single background worker (ADR-0003): one job at a time, so QuickBooks calls and ledger writes are serialised.
/// M14 §5: it also runs the scheduled list sync, in the same loop — it waits for the next item or the due time,
/// whichever comes first, and a waiting item always goes first. No timer, no second hosted service.
/// </summary>
/// <param name="delay">
/// The wait for the due time; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test replaces it. It must
/// honour its token.
/// </param>
/// <param name="companies">
/// M17 §4.7: with several companies (<see cref="CompanySweepSource.Enabled"/>) a scheduled attempt is a sweep of the
/// companies <c>Companies:ScheduledSync</c> selects, one at a time; otherwise (or null) the default company as before.
/// </param>
public sealed class JobWorker(
    JobQueue queue,
    IJobProcessor processor,
    IJobStore store,
    ILogger<JobWorker> log,
    ListSyncSchedule schedule,
    QbListSync listSync,
    IQbPresence presence,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    CompanySweepSource? companies = null)
    : BackgroundService
{
    public const string InterruptedPosting = "interrupted — run duplicates before re-post";

    /// <summary>The log correlation id of a scheduled sync attempt.</summary>
    public const string ScheduledSyncLabel = "qb-sync-lists-schedule";

    /// <summary>§12: who a scheduled attempt is "by" in its log line (it has no requesting user).</summary>
    public const string ScheduledRequestedBy = "system";

    /// <summary>The longest single wait for the due time; a longer one is taken in steps.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    /// <summary>
    /// The wait before a deferred scheduled attempt tries again (§5.9): the work that held the slot is normally already
    /// in the channel and wakes the worker at once; this only bounds the instant between a claim and its enqueue.
    /// </summary>
    public static readonly TimeSpan DeferredRetry = TimeSpan.FromMilliseconds(250);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>M17 §4.7: the multi-company sweep in progress, or null. Only the worker's loop reads or writes it.</summary>
    private SweepCursor? _sweep;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        schedule.WorkerStarted();
        try
        {
            if (!schedule.Enabled)
            {
                // IntervalMinutes = 0: exactly the loop before M14.
                await foreach (var item in queue.ReadAllAsync(stoppingToken))
                {
                    await RunOneAsync(item, stoppingToken);
                }

                return;
            }

            var fallback = false;
            var deferred = false;
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // At most MaxWait at a time: Task.Delay refuses more than ~24.8 days (a future FinishedUtc from a
                // skewed clock gets there), and the loop re-checks the due time on every wake anyway. A sweep in
                // progress is due at once: its next company follows whatever is waiting.
                var wait = _sweep is not null ? TimeSpan.Zero : schedule.TimeUntilDue() ?? TimeSpan.Zero;
                wait = wait > MaxWait ? MaxWait : wait;
                var delayFor = fallback ? Task.Delay : _delay;
                fallback = false;
                Func<CancellationToken, Task>? deadline = wait > TimeSpan.Zero ? token => delayFor(wait, token) : null;
                if (deadline is null && deferred)
                {
                    // The last attempt was deferred because the slot was taken: wait for that work (or a short while)
                    // rather than spin on a claim whose item is not in the channel yet.
                    deadline = token => Task.Delay(DeferredRetry, token);
                }

                deferred = false;
                bool ran;
                try
                {
                    ran = await queue.RunNextAsync(item => RunOneAsync(item, stoppingToken), deadline, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // A broken wait must not stop the worker. The next wait uses Task.Delay, so a delay that always
                    // fails cannot spin the loop; the one after that tries the configured delay again.
                    log.LogWarning(ex, "Scheduled list sync: the wait failed; waiting once with the built-in delay");
                    fallback = true;
                    continue;
                }

                if (ran)
                {
                    continue; // re-check: another item may be waiting, and an attempt of any trigger moves the due time
                }

                if (_sweep is null && schedule.TimeUntilDue() is { } left && left > TimeSpan.Zero)
                {
                    continue; // woke early, or the due time moved while waiting
                }

                deferred = !await RunScheduledSyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping; a job left in analysing/posting is fixed by startup recovery.
            CancelSweep(stoppingToken);
        }
    }

    /// <summary>
    /// M14 §5.4–5.6, §5.9, §6: one scheduled attempt. Never throws except for the host stopping; a failure was already
    /// recorded by <see cref="QbListSync"/> and never touches a job. False when the attempt was deferred (nothing ran,
    /// nothing recorded): the exclusive slot was taken, or a dashboard sync is queued.
    /// </summary>
    private async Task<bool> RunScheduledSyncAsync(CancellationToken stoppingToken)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["jobId"] = ScheduledSyncLabel });

        // §5.9 (ADR-0012 with ADR-0008): the sync holds the exclusive slot while it runs, so a post that must not wait
        // is answered 409 rather than queued behind a QuickBooks dialog. Slot taken: that work runs first; no row (§5.3).
        if (!queue.TryClaimIdle())
        {
            log.LogDebug("Scheduled list sync deferred: the QuickBooks session is in use");
            return false;
        }

        var claimed = 1;
        void Release()
        {
            if (Interlocked.Exchange(ref claimed, 0) == 1)
            {
                queue.ReleaseIdleClaim();
            }
        }

        try
        {
            return await RunClaimedAttemptAsync(Release, stoppingToken);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// The attempt itself, with the slot held. <paramref name="release"/> frees the slot before the attempt's result is
    /// published (as <see cref="JobQueue"/>'s exclusive work does), so a joiner who has its answer is never told "busy"
    /// by the sync it has just waited for.
    /// </summary>
    private async Task<bool> RunClaimedAttemptAsync(Action release, CancellationToken stoppingToken)
    {
        if (companies is { Enabled: true } sweep)
        {
            return await RunSweepStepAsync(sweep, release, stoppingToken);
        }

        var warnOnFailure = schedule.LastKnownWasOk;
        var started = schedule.UtcNow;
        if (schedule.BeginAttempt() is not { } attempt)
        {
            // M14 §7.2: a dashboard sync is in flight; its item is already in the channel, so the loop runs it next.
            log.LogDebug("Scheduled list sync deferred: a dashboard sync is queued");
            return false;
        }

        var outcome = ListSyncOutcome.Failed;
        try
        {
            if (!ListSyncGuard.IsOpen(presence, log))
            {
                outcome = ListSyncOutcome.Skipped;
                await ListSyncGuard.RecordSkippedAsync(listSync, ListSyncTrigger.Schedule, null, log, stoppingToken);
                LogNotOk(warnOnFailure, outcome, ListSyncReasons.QuickBooksNotOpen, "QuickBooks is not running in this session");
                release();
                attempt.TrySetResult(null);
                return true;
            }

            try
            {
                var completion = await listSync.SyncWithRunAsync(ListSyncTrigger.Schedule, null, stoppingToken);
                var result = completion.Result;
                outcome = ListSyncOutcome.Ok;
                log.LogInformation(
                    "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome}, {Accounts} accounts, {Vendors} vendors, {Customers} customers",
                    Lower(ListSyncTrigger.Schedule), ScheduledRequestedBy, Lower(outcome), result.Accounts, result.Vendors, result.Customers);
                if (result.MissingInRules.Count > 0)
                {
                    log.LogWarning("Names in rules.json that QuickBooks does not have: {Missing}", string.Join("; ", result.MissingInRules));
                }

                release();
                attempt.TrySetResult(completion);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                release();
                attempt.TrySetCanceled(stoppingToken);
                throw;
            }
            catch (Exception ex)
            {
                var recorded = schedule.RecordedLastAttempt;
                // The row this attempt recorded: a scheduled failure that started no earlier than this attempt did.
                var reason = recorded is { Trigger: ListSyncTrigger.Schedule, Outcome: ListSyncOutcome.Failed } r && r.StartedUtc >= started
                    ? r.Reason
                    : null;
                LogNotOk(warnOnFailure, ListSyncOutcome.Failed, reason, ex.Message);
                release();
                attempt.TrySetException(ex);
                _ = attempt.Task.Exception; // observed: a joiner may never come
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            release();
            attempt.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (Exception ex)
        {
            // Never crash the loop over a sync.
            log.LogError(ex, "Scheduled list sync crashed");
            release();
            attempt.TrySetException(ex);
            _ = attempt.Task.Exception;
        }
        finally
        {
            schedule.EndAttempt(outcome);
        }

        return true;
    }

    /// <summary>
    /// M17 §4.7: one step of a scheduled sweep, with the slot held for that step only. The first step begins the
    /// attempt (nobody joins it), chooses the companies and checks QuickBooks once: not open is a skipped
    /// <c>quickbooks-not-open</c> row for every selected company and ends the attempt. Otherwise each step syncs the next
    /// company of the cursor from its own file into its own lists, then gives the slot back, so a post is refused for one
    /// company's sync at most and anything queued meanwhile (a job, a Sync now) runs before the next company. A company
    /// that fails or cannot be opened (its skipped row is recorded by <see cref="QbListSync"/>) is logged and the sweep
    /// goes on. The attempt ends with the last company: Ok only when every selected company synced.
    /// </summary>
    private async Task<bool> RunSweepStepAsync(CompanySweepSource sweep, Action release, CancellationToken stoppingToken)
    {
        if (_sweep is null)
        {
            var warnOnFailure = schedule.LastKnownWasOk;
            if (schedule.BeginAttempt(null) is not { } attempt)
            {
                log.LogDebug("Scheduled list sync deferred: a dashboard sync is queued");
                return false;
            }

            IReadOnlyList<CompanyContext> selected;
            try
            {
                selected = await sweep.SelectAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                release();
                attempt.TrySetCanceled(stoppingToken);
                schedule.EndAttempt(ListSyncOutcome.Failed);
                throw;
            }
            catch (Exception ex)
            {
                // The companies could not be read (the database is down): nothing was synced. Never crash the loop.
                log.LogError(ex, "Scheduled list sync: the companies to sync could not be chosen");
                release();
                attempt.TrySetException(ex);
                _ = attempt.Task.Exception;
                schedule.EndAttempt(ListSyncOutcome.Failed);
                return true;
            }

            if (selected.Count == 0)
            {
                log.LogInformation("Scheduled list sync ({Mode}): no company to sync", sweep.Mode);
                EndSweep(attempt, release, ListSyncOutcome.Ok);
                return true;
            }

            if (!ListSyncGuard.IsOpen(presence, log))
            {
                foreach (var company in selected)
                {
                    await RecordNotOpenAsync(company, stoppingToken);
                }

                LogNotOk(warnOnFailure, ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen, $"QuickBooks is not running in this session ({selected.Count} companies)");
                EndSweep(attempt, release, ListSyncOutcome.Skipped);
                return true;
            }

            _sweep = new SweepCursor(attempt, selected, warnOnFailure);
        }

        var cursor = _sweep;
        var next = cursor.Remaining.Dequeue();
        try
        {
            if (await SyncCompanyAsync(next, cursor.WarnOnFailure, stoppingToken))
            {
                cursor.Synced++;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            CancelSweep(stoppingToken);
            throw;
        }

        if (cursor.Remaining.Count == 0)
        {
            _sweep = null;
            EndSweep(cursor.Attempt, release, cursor.Synced == cursor.Selected ? ListSyncOutcome.Ok : ListSyncOutcome.Failed);
        }

        return true;
    }

    /// <summary>Frees the slot, then publishes the end of the attempt (nobody joins a sweep) and moves the due time.</summary>
    private void EndSweep(TaskCompletionSource<ListSyncCompletion?> attempt, Action release, ListSyncOutcome outcome)
    {
        release();
        attempt.TrySetResult(null);
        schedule.EndAttempt(outcome);
    }

    /// <summary>The host is stopping with a sweep part done: the attempt ends cancelled; the next start sweeps again.</summary>
    private void CancelSweep(CancellationToken stoppingToken)
    {
        if (_sweep is not { } cursor)
        {
            return;
        }

        _sweep = null;
        cursor.Attempt.TrySetCanceled(stoppingToken);
        schedule.EndAttempt(ListSyncOutcome.Failed);
    }

    /// <summary>The sweep in progress: the companies still to sync, one per step. Touched only by the worker's loop.</summary>
    private sealed class SweepCursor(
        TaskCompletionSource<ListSyncCompletion?> attempt, IReadOnlyList<CompanyContext> selected, bool warnOnFailure)
    {
        public TaskCompletionSource<ListSyncCompletion?> Attempt { get; } = attempt;

        public Queue<CompanyContext> Remaining { get; } = new(selected);

        public int Selected { get; } = selected.Count;

        public bool WarnOnFailure { get; } = warnOnFailure;

        public int Synced { get; set; }
    }

    /// <summary>One company of a sweep. False when it did not sync; its row was recorded by <see cref="QbListSync"/>.</summary>
    private async Task<bool> SyncCompanyAsync(CompanyContext company, bool warnOnFailure, CancellationToken stoppingToken)
    {
        try
        {
            var result = (await listSync.SyncWithRunAsync(company, ListSyncTrigger.Schedule, null, stoppingToken)).Result;
            log.LogInformation(
                "QuickBooks lists sync ({Trigger}, by {RequestedBy}, company {CompanyId}): {Outcome}, {Accounts} accounts, {Vendors} vendors, {Customers} customers",
                Lower(ListSyncTrigger.Schedule), ScheduledRequestedBy, company.Id, Lower(ListSyncOutcome.Ok), result.Accounts, result.Vendors, result.Customers);
            if (result.MissingInRules.Count > 0)
            {
                log.LogWarning("Names in the rules of company {CompanyId} that QuickBooks does not have: {Missing}", company.Id, string.Join("; ", result.MissingInRules));
            }

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var refusal = CompanyFileRefusal.Of(ex);
            log.Log(
                warnOnFailure ? LogLevel.Warning : LogLevel.Information,
                "QuickBooks lists sync ({Trigger}, by {RequestedBy}, company {CompanyId}): {Outcome} ({Reason}): {Error}",
                Lower(ListSyncTrigger.Schedule), ScheduledRequestedBy, company.Id,
                Lower(refusal is null ? ListSyncOutcome.Failed : ListSyncOutcome.Skipped), refusal?.Type ?? QbListSync.ReasonOf(ex) ?? "error", ex.Message);
            return false;
        }
    }

    /// <summary>A skipped <c>quickbooks-not-open</c> row in the company's history; a failure to record it is only logged.</summary>
    private async Task RecordNotOpenAsync(CompanyContext company, CancellationToken stoppingToken)
    {
        try
        {
            await listSync.RecordSkippedAsync(company, ListSyncTrigger.Schedule, null, ListSyncReasons.QuickBooksNotOpen, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("QuickBooks lists sync: the skipped attempt of company {CompanyId} could not be recorded: {Error}", company.Id, ex.Message);
        }
    }

    /// <summary>§5.6: Warning for the first not-Ok attempt after an Ok (or the first ever), Information after that.</summary>
    private void LogNotOk(bool warn, ListSyncOutcome outcome, string? reason, string error) =>
        log.Log(
            warn ? LogLevel.Warning : LogLevel.Information,
            "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome} ({Reason}): {Error}",
            Lower(ListSyncTrigger.Schedule), ScheduledRequestedBy, Lower(outcome), reason ?? "error", error);

    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    private async Task RunOneAsync(JobWorkItem item, CancellationToken stoppingToken)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["jobId"] = item.JobId });
        try
        {
            log.LogInformation("Job {JobId}: {Action} started", item.JobId, item.Action);
            if (item.Action == JobAction.Exclusive)
            {
                // Not a job: the work reports its own errors to the caller and never changes a job's status here.
                await item.Work!(stoppingToken);
                log.LogInformation("Job {JobId}: {Action} finished", item.JobId, item.Action);
                return;
            }

            await processor.ProcessAsync(item, stoppingToken);
            log.LogInformation("Job {JobId}: {Action} finished with status {Status}", item.JobId, item.Action, store.Get(item.JobId)?.Status);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {JobId}: {Action} crashed", item.JobId, item.Action);
            FailSafe(item.JobId, ex);
        }
    }

    /// <summary>Last resort when the processor itself throws: never leave a job looking active.</summary>
    private void FailSafe(string jobId, Exception ex)
    {
        var record = store.Get(jobId);
        var next = record?.Status switch
        {
            JobStatus.Queued or JobStatus.Analysing => JobStatus.Failed,
            JobStatus.Posting => JobStatus.Partial,
            _ => (JobStatus?)null,
        };

        if (record is null || next is null)
        {
            return;
        }

        var error = next == JobStatus.Partial
            ? $"{InterruptedPosting}: {ex.Message}"
            : $"internal error: {ex.Message}";
        store.Save(record.MoveTo(next.Value, error));
    }
}
