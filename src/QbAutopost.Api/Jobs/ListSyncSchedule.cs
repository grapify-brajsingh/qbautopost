using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// M14 §5: when the scheduled list sync is due, and the handle of the one running now. Shared by <see cref="JobWorker"/>
/// (which runs the sync) and the v2 lists routes (which show <see cref="NextSyncUtc"/> and join <see cref="InFlight"/>).
/// M14 §7.2: <see cref="InFlight"/> is the one single-flight handle for both triggers; a dashboard sync takes it through
/// <see cref="JoinOrStart"/>, a scheduled attempt through <c>BeginAttempt</c>, and neither starts while the other holds it.
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
    private Task<ListSyncCompletion?>? _inFlight;
    private Task<ListSyncCompletion?>? _scheduled;

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
    /// The list sync in flight now, or null: the scheduled attempt the worker is running, or a dashboard sync that is
    /// queued or running (<see cref="JoinOrStart"/>). Its result is the sync's answer, null when a scheduled attempt was
    /// skipped, faulted with the sync's exception when it failed, cancelled when the host stopped it. It completes
    /// before it is cleared, so a caller who read it can always await it.
    /// </summary>
    public Task<ListSyncCompletion?>? InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// M14 §7.2: the dashboard's single flight. When a sync is in flight (<see cref="InFlight"/>: scheduled, or another
    /// dashboard request's), returns it with <c>Joined</c> true and starts nothing. Otherwise the new flight becomes
    /// <see cref="InFlight"/> and <paramref name="enqueue"/> is called, under this schedule's lock, with the work that
    /// runs <paramref name="sync"/>; it must only <b>enqueue</b> that work on the worker (never run it inline), so the
    /// worker, which checks this lock before a scheduled attempt, always finds the item already in its channel.
    /// <para>
    /// The flight completes (result, fault, or cancellation) and is cleared inside that work, on the worker, before the
    /// worker moves on. A caller's own wait bound is applied by the caller; it never cancels the flight.
    /// </para>
    /// </summary>
    public (Task<ListSyncCompletion?> Flight, bool Joined) JoinOrStart(
        Func<CancellationToken, Task<ListSyncCompletion>> sync, Action<Func<CancellationToken, Task<ListSyncCompletion>>> enqueue)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(enqueue);
        lock (_gate)
        {
            if (_inFlight is { } running)
            {
                return (running, true);
            }

            var flight = new TaskCompletionSource<ListSyncCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _inFlight, flight.Task);
            try
            {
                enqueue(ct => RunFlightAsync(flight, sync, ct));
            }
            catch
            {
                Volatile.Write(ref _inFlight, null);
                throw;
            }

            return (flight.Task, false);
        }
    }

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

    /// <summary>The schedule's clock, so the worker times an attempt as <see cref="QbListSync"/> does.</summary>
    internal DateTime UtcNow => clock.UtcNow;

    /// <summary>The source's newest recorded attempt (any trigger).</summary>
    internal ListSyncRun? RecordedLastAttempt => source.LastAttempt;

    /// <summary>
    /// Starts a scheduled attempt: <see cref="InFlight"/> becomes the returned source's task. Null when a dashboard
    /// sync is already in flight (<see cref="JoinOrStart"/>): its item is in the worker's channel, so the worker runs it
    /// next instead, records nothing for this pass (§5.3), and the attempt it records moves the due time.
    /// </summary>
    /// <remarks>Called by <see cref="JobWorker"/> only; public so the single-flight rule can be tested on its own.</remarks>
    public TaskCompletionSource<ListSyncCompletion?>? BeginAttempt()
    {
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                return null;
            }

            var attempt = new TaskCompletionSource<ListSyncCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scheduled = attempt.Task;
            Volatile.Write(ref _inFlight, attempt.Task);
            return attempt;
        }
    }

    /// <summary>
    /// Ends a scheduled attempt: remembers when it finished and how (the next due time moves even if it could not be
    /// recorded) and clears <see cref="InFlight"/>. The caller completes the attempt's task first.
    /// </summary>
    /// <remarks>Called by <see cref="JobWorker"/> only; public for the same reason as <see cref="BeginAttempt"/>.</remarks>
    public void EndAttempt(ListSyncOutcome outcome)
    {
        lock (_gate)
        {
            _ownLast = new Attempt(clock.UtcNow, outcome);
            if (_scheduled is { } scheduled)
            {
                Clear(scheduled);
                _scheduled = null;
            }
        }
    }

    /// <summary>The work of one dashboard flight, on the worker: completes the flight, then clears it.</summary>
    private async Task<ListSyncCompletion> RunFlightAsync(
        TaskCompletionSource<ListSyncCompletion?> flight, Func<CancellationToken, Task<ListSyncCompletion>> sync, CancellationToken ct)
    {
        try
        {
            var result = await sync(ct);
            flight.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            flight.TrySetCanceled(ct);
            throw;
        }
        catch (Exception ex)
        {
            flight.TrySetException(ex);
            _ = flight.Task.Exception; // observed: every joiner may have stopped waiting
            throw;
        }
        finally
        {
            lock (_gate)
            {
                Clear(flight.Task);
            }
        }
    }

    /// <summary>Clears <see cref="InFlight"/> when it is still <paramref name="task"/>. Called under the lock.</summary>
    private void Clear(Task<ListSyncCompletion?> task)
    {
        if (ReferenceEquals(_inFlight, task))
        {
            Volatile.Write(ref _inFlight, null);
        }
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
