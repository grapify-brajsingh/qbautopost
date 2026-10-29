using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// M14 §5: when the scheduled list sync is due, and the handle of the one running now. Shared by <see cref="JobWorker"/>
/// (which runs the sync) and the v2 lists routes (which show <see cref="NextSyncUtc"/> and join <see cref="InFlight"/>).
/// <para>
/// Due = <c>max(last attempt finished + interval, worker start + FirstDelay)</c>, or <c>worker start + FirstDelay</c>
/// when no attempt is known. "Last attempt" is the newest of the source's <see cref="IQbListsSource.LastAttempt"/> (any
/// trigger, any outcome) and the last scheduled attempt this process made: when the attempt could not be recorded (the
/// database is down) the source does not move, and without the second the worker would try again at once, forever.
/// </para>
/// </summary>
public sealed class ListSyncSchedule(ListSyncSettings settings, IQbListsSource source, IClock clock)
{
    private readonly object _gate = new();
    private DateTime? _workerStartedUtc;
    private Attempt? _ownLast;
    private Task<ListSyncResult?>? _inFlight;

    /// <summary>False when <c>IntervalMinutes</c> is 0: the worker never syncs on its own.</summary>
    public bool Enabled => settings.IntervalMinutes > 0;

    public TimeSpan Interval => TimeSpan.FromMinutes(settings.IntervalMinutes);

    public TimeSpan FirstDelay => TimeSpan.FromMinutes(settings.FirstDelayMinutes);

    /// <summary>When the worker's loop started; null before that.</summary>
    public DateTime? WorkerStartedUtc
    {
        get
        {
            lock (_gate)
            {
                return _workerStartedUtc;
            }
        }
    }

    /// <summary>When the next scheduled sync is due; null when the schedule is off or the worker has not started.</summary>
    public DateTime? NextSyncUtc
    {
        get
        {
            lock (_gate)
            {
                if (!Enabled || _workerStartedUtc is not { } started)
                {
                    return null;
                }

                var floor = started + FirstDelay;
                return LatestKnown() is { } last ? Max(last.FinishedUtc + Interval, floor) : floor;
            }
        }
    }

    /// <summary>
    /// The scheduled attempt running now, or null. Its result is the sync's answer, null when the attempt was skipped,
    /// faulted with the sync's exception when it failed, cancelled when the host stopped it. It completes before it is
    /// cleared, so a caller who read it can always await it.
    /// </summary>
    public Task<ListSyncResult?>? InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Time until <see cref="NextSyncUtc"/> (zero or less when due); null when there is no schedule.</summary>
    public TimeSpan? TimeUntilDue() => NextSyncUtc is { } due ? due - clock.UtcNow : null;

    /// <summary>Called once by the worker when its loop starts; later calls keep the first time.</summary>
    public void WorkerStarted()
    {
        lock (_gate)
        {
            _workerStartedUtc ??= clock.UtcNow;
        }
    }

    /// <summary>
    /// True when a failure now would start a failure streak (§5.6): the newest known attempt was Ok, or none is known.
    /// Skipped counts as not Ok.
    /// </summary>
    public bool LastKnownWasOk
    {
        get
        {
            lock (_gate)
            {
                return LatestKnown() is not { } last || last.Outcome == ListSyncOutcome.Ok;
            }
        }
    }

    /// <summary>The source's newest recorded attempt (any trigger).</summary>
    internal ListSyncRun? RecordedLastAttempt => source.LastAttempt;

    /// <summary>Starts a scheduled attempt: <see cref="InFlight"/> becomes the returned source's task.</summary>
    internal TaskCompletionSource<ListSyncResult?> BeginAttempt()
    {
        var attempt = new TaskCompletionSource<ListSyncResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _inFlight, attempt.Task);
        return attempt;
    }

    /// <summary>
    /// Ends a scheduled attempt: remembers when it finished and how (the next due time moves even if it could not be
    /// recorded) and clears <see cref="InFlight"/>. The caller completes the attempt's task first.
    /// </summary>
    internal void EndAttempt(ListSyncOutcome outcome)
    {
        lock (_gate)
        {
            _ownLast = new Attempt(clock.UtcNow, outcome);
        }

        Volatile.Write(ref _inFlight, null);
    }

    // SPEC-GAP T-1406 (Q-122): the schedule's own last attempt counts too, so an unrecordable one still waits an interval.
    private Attempt? LatestKnown()
    {
        var recorded = source.LastAttempt is { } run ? new Attempt(run.FinishedUtc, run.Outcome) : null;
        if (recorded is null)
        {
            return _ownLast;
        }

        return _ownLast is { } own && own.FinishedUtc > recorded.FinishedUtc ? own : recorded;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private sealed record Attempt(DateTime FinishedUtc, ListSyncOutcome Outcome);
}
