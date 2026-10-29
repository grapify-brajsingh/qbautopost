using QbAutopost.Core.Abstractions;
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
public sealed class JobWorker(
    JobQueue queue,
    IJobProcessor processor,
    IJobStore store,
    ILogger<JobWorker> log,
    ListSyncSchedule schedule,
    QbListSync listSync,
    IQbPresence presence,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
    : BackgroundService
{
    public const string InterruptedPosting = "interrupted — run duplicates before re-post";

    /// <summary>The log correlation id of a scheduled sync attempt.</summary>
    public const string ScheduledSyncLabel = "qb-sync-lists-schedule";

    /// <summary>§12: who a scheduled attempt is "by" in its log line (it has no requesting user).</summary>
    public const string ScheduledRequestedBy = "system";

    /// <summary>The longest single wait for the due time; a longer one is taken in steps.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

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
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // At most MaxWait at a time: Task.Delay refuses more than ~24.8 days (a future FinishedUtc from a
                // skewed clock gets there), and the loop re-checks the due time on every wake anyway.
                var wait = schedule.TimeUntilDue() ?? TimeSpan.Zero;
                wait = wait > MaxWait ? MaxWait : wait;
                var delayFor = fallback ? Task.Delay : _delay;
                fallback = false;
                Func<CancellationToken, Task>? deadline = wait > TimeSpan.Zero ? token => delayFor(wait, token) : null;
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

                if (schedule.TimeUntilDue() is { } left && left > TimeSpan.Zero)
                {
                    continue; // woke early, or the due time moved while waiting
                }

                await RunScheduledSyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping; a job left in analysing/posting is fixed by startup recovery.
        }
    }

    /// <summary>
    /// M14 §5.4–5.6, §6: one scheduled attempt. Never throws except for the host stopping; a failure was already recorded
    /// by <see cref="QbListSync"/> and never touches a job.
    /// </summary>
    private async Task RunScheduledSyncAsync(CancellationToken stoppingToken)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["jobId"] = ScheduledSyncLabel });
        var warnOnFailure = schedule.LastKnownWasOk;
        var started = schedule.UtcNow;
        var attempt = schedule.BeginAttempt();
        var outcome = ListSyncOutcome.Failed;
        try
        {
            if (!IsOpen())
            {
                outcome = ListSyncOutcome.Skipped;
                await RecordSkippedAsync(stoppingToken);
                LogNotOk(warnOnFailure, outcome, ListSyncReasons.QuickBooksNotOpen, "QuickBooks is not running in this session");
                attempt.TrySetResult(null);
                return;
            }

            try
            {
                var result = await listSync.SyncAsync(ListSyncTrigger.Schedule, null, stoppingToken);
                outcome = ListSyncOutcome.Ok;
                log.LogInformation(
                    "QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome}, {Accounts} accounts, {Vendors} vendors, {Customers} customers",
                    Lower(ListSyncTrigger.Schedule), ScheduledRequestedBy, Lower(outcome), result.Accounts, result.Vendors, result.Customers);
                if (result.MissingInRules.Count > 0)
                {
                    log.LogWarning("Names in rules.json that QuickBooks does not have: {Missing}", string.Join("; ", result.MissingInRules));
                }

                attempt.TrySetResult(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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
                attempt.TrySetException(ex);
                _ = attempt.Task.Exception; // observed: a joiner may never come
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            attempt.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (Exception ex)
        {
            // Never crash the loop over a sync.
            log.LogError(ex, "Scheduled list sync crashed");
            attempt.TrySetException(ex);
            _ = attempt.Task.Exception;
        }
        finally
        {
            schedule.EndAttempt(outcome);
        }
    }

    /// <summary>§6: never start QuickBooks. A check that itself fails counts as "not open" — hold, don't guess.</summary>
    // SPEC-GAP T-1406 (Q-122): a throwing check is "not open".
    private bool IsOpen()
    {
        try
        {
            return presence.IsCompanyOpen();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "QuickBooks presence check failed; the scheduled list sync is skipped");
            return false;
        }
    }

    private async Task RecordSkippedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await listSync.RecordSkippedAsync(ListSyncTrigger.Schedule, null, ListSyncReasons.QuickBooksNotOpen, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // The store has logged it (database mode); the schedule still moves the due time on.
            log.LogInformation("Scheduled list sync: the skipped attempt could not be recorded: {Error}", ex.Message);
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
