using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Jobs;

/// <summary>M14 §5, §6, §12: the scheduled list sync inside <see cref="JobWorker"/>, driven by a fake clock and delay.</summary>
public sealed class ScheduledListSyncTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();
    private readonly JobQueue _queue = new();
    private readonly FixedClock _clock = new();
    private readonly FakeQbGateway _gateway = new();
    private readonly FakeQbPresence _presence = new();
    private readonly ManualDelay _delay = new();
    private readonly ListLogger<JobWorker> _log = new();
    private readonly RecordingProcessor _processor = new();
    private readonly JobStore _store;
    private readonly FileQbLists _lists;

    public ScheduledListSyncTests()
    {
        var settings = new AppSettings { Paths = { JobIndex = _dir.Combine("jobs.json") } };
        _store = new JobStore(Options.Create(settings), _clock, NullLogger<JobStore>.Instance);
        _lists = new FileQbLists(_dir.Combine("qb-lists.json"));
    }

    public void Dispose() => _dir.Dispose();

    private DateTime Start => new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private QbListSync Sync(IQbGateway? gateway = null, IQbListsStore? lists = null) =>
        Workers.Sync(_dir.Path, gateway ?? _gateway, _clock, lists ?? _lists);

    private ListSyncSchedule Schedule(int intervalMinutes = 30, IQbListsSource? source = null, int firstDelayMinutes = 2) =>
        new(new ListSyncSettings { IntervalMinutes = intervalMinutes, FirstDelayMinutes = firstDelayMinutes }, source ?? _lists, _clock);

    private JobWorker Worker(ListSyncSchedule schedule, QbListSync? sync = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_queue, _processor, _store, _log, schedule, sync ?? Sync(), _presence, delay ?? _delay.Delay);

    private IReadOnlyList<LogEntry> SyncLines =>
        _log.Entries.Where(e => e.Message.StartsWith("QuickBooks lists sync (schedule", StringComparison.Ordinal)).ToList();

    /// <summary>Lets the pending wait end at its due time, then returns the worker's next wait.</summary>
    private async Task<ManualDelay.Pending> ElapseAsync(ManualDelay.Pending pending)
    {
        _clock.UtcNow += pending.Wait;
        pending.Release();
        return await _delay.NextAsync();
    }

    private static Task RecordAsync(IQbListsStore lists, DateTime finished, ListSyncOutcome outcome = ListSyncOutcome.Ok) =>
        lists.RecordAsync(
            new ListSyncRun { Trigger = ListSyncTrigger.Api, StartedUtc = finished, FinishedUtc = finished, Outcome = outcome },
            null,
            CancellationToken.None);

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(done(), "the expected state was not reached in time");
    }

    // --- due time ---------------------------------------------------------------------------------------------

    [Fact]
    public void Should_BeDueAtStartPlusFirstDelay_When_NoAttemptIsKnown()
    {
        var schedule = Schedule();
        schedule.WorkerStarted();

        Assert.Equal(Start + FirstDelay, schedule.NextSyncUtc);
    }

    [Fact]
    public async Task Should_BeDueOneIntervalAfterTheLastAttempt_When_ThatIsLater()
    {
        var schedule = Schedule();
        schedule.WorkerStarted();
        await RecordAsync(_lists, Start.AddMinutes(10), ListSyncOutcome.Failed);

        Assert.Equal(Start.AddMinutes(10) + Interval, schedule.NextSyncUtc);
    }

    [Fact]
    public async Task Should_WaitForTheFirstDelay_When_TheLastAttemptIsOld()
    {
        await RecordAsync(_lists, Start.AddHours(-5));
        var schedule = Schedule();
        schedule.WorkerStarted();

        Assert.Equal(Start + FirstDelay, schedule.NextSyncUtc); // max(old + 30 min, start + 2 min)
    }

    [Fact]
    public void Should_HaveNoDueTime_When_TheIntervalIsZero()
    {
        var schedule = Schedule(intervalMinutes: 0);
        schedule.WorkerStarted();

        Assert.False(schedule.Enabled);
        Assert.Null(schedule.NextSyncUtc);
    }

    [Fact]
    public void Should_HaveNoDueTime_When_TheWorkerHasNotStarted() =>
        Assert.Null(Schedule().NextSyncUtc);

    // --- the loop ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_SyncOnceAndRecordScheduleOk_When_DueAndQuickBooksIsOpen()
    {
        var schedule = Schedule();
        using var worker = Worker(schedule);
        await worker.StartAsync(CancellationToken.None);

        var first = await _delay.NextAsync();
        Assert.Equal(FirstDelay, first.Wait);
        var next = await ElapseAsync(first);

        Assert.Single(_gateway.Queries);
        Assert.Equal(1, _presence.Calls);
        var last = _lists.LastAttempt!;
        Assert.Equal((ListSyncTrigger.Schedule, ListSyncOutcome.Ok, null), (last.Trigger, last.Outcome, last.RequestedBy));
        Assert.Equal(Interval, next.Wait);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_LogOneInformationLine_When_AScheduledSyncSucceeds()
    {
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);

        await ElapseAsync(await _delay.NextAsync());

        var line = Assert.Single(SyncLines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.StartsWith("QuickBooks lists sync (schedule, by system): ok, ", line.Message, StringComparison.Ordinal);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_RunTheJobFirstAndThenSync_When_AJobIsQueuedAtTheDueTime()
    {
        _processor.OnProcess = () => _processor.QueriesSeen = _gateway.Queries.Count;
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);
        var first = await _delay.NextAsync();

        _clock.UtcNow += first.Wait; // due now
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        first.Release();
        await _delay.NextAsync();

        Assert.Equal(["a:Analyse"], _processor.Seen);
        Assert.Equal(0, _processor.QueriesSeen); // the job ran before the sync
        Assert.Single(_gateway.Queries); // and the sync followed once the queue was empty
        Assert.Equal(0, _queue.Outstanding);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_RunAJobQueuedDuringAnotherJobFirst_When_TheSyncBecameDueMeanwhile()
    {
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queriesAtB = -1;
        _processor.Hold = item =>
        {
            if (item.JobId == "a")
            {
                aStarted.TrySetResult();
                return releaseA.Task;
            }

            queriesAtB = _gateway.Queries.Count;
            return Task.CompletedTask;
        };
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);
        await _delay.NextAsync();

        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        await aStarted.Task.WaitAsync(Patience); // A holds the worker
        _clock.UtcNow += TimeSpan.FromMinutes(3); // the sync is now due
        _queue.Enqueue(new JobWorkItem("b", JobAction.Analyse));
        releaseA.TrySetResult();
        await _delay.NextAsync(); // the sync has run and the worker waits again

        Assert.Equal(["a:Analyse", "b:Analyse"], _processor.Seen);
        Assert.Equal(0, queriesAtB); // B ran before any gateway query
        Assert.Single(_gateway.Queries);
    }

    [Fact]
    public async Task Should_RunTheWaitingItemBeforeTheSync_When_TheSyncIsDueAtOnce()
    {
        // FirstDelay 0: due the moment the worker starts, and an item is already waiting — no race, no wait at all.
        _processor.OnProcess = () => _processor.QueriesSeen = _gateway.Queries.Count;
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        using var worker = Worker(Schedule(firstDelayMinutes: 0));
        await worker.StartAsync(CancellationToken.None);

        var next = await _delay.NextAsync();

        Assert.Equal(["a:Analyse"], _processor.Seen);
        Assert.Equal(0, _processor.QueriesSeen);
        Assert.Single(_gateway.Queries);
        Assert.Equal(Interval, next.Wait);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_WaitAtMostOneHourAtATime_When_TheDueTimeIsFarAway()
    {
        await RecordAsync(_lists, Start.AddDays(100)); // a clock skewed far into the future
        using var worker = Worker(Schedule(intervalMinutes: 60 * 24 * 100));
        await worker.StartAsync(CancellationToken.None);

        var first = await _delay.NextAsync();
        var second = await ElapseAsync(first); // an early wake: re-checks and waits again

        Assert.Equal(JobWorker.MaxWait, first.Wait);
        Assert.Equal(JobWorker.MaxWait, second.Wait);
        Assert.Empty(_gateway.Requests);
        Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_NotThrow_When_TheRealDelayIsGivenAHugeInterval()
    {
        await RecordAsync(_lists, Start.AddYears(5));
        using var worker = Worker(Schedule(intervalMinutes: int.MaxValue / 2), delay: Task.Delay);
        await worker.StartAsync(CancellationToken.None);
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));

        await UntilAsync(() => _processor.Seen.Count == 1);

        Assert.False(worker.ExecuteTask!.IsCompleted); // Task.Delay above ~24.8 days would have thrown
        await worker.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_KeepRunning_When_TheDelayFaultsOrIsCancelledByItself(bool cancelled)
    {
        var calls = 0;
        using var worker = Worker(Schedule(), delay: (wait, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return cancelled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new InvalidOperationException("broken delay"));
            }

            return _delay.Delay(wait, ct);
        });
        await worker.StartAsync(CancellationToken.None);
        await UntilAsync(() => _log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("wait failed", StringComparison.Ordinal)));

        // The worker now waits on its fallback (the real delay); a job still wakes it, then it waits on the delay again.
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        var next = await _delay.NextAsync();

        Assert.Equal(["a:Analyse"], _processor.Seen);
        Assert.False(worker.ExecuteTask!.IsCompleted);
        Assert.Empty(_gateway.Requests); // a broken wait is not "due"
        Assert.Equal(FirstDelay, next.Wait);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_NotCountTheSyncAsQueuedWork_When_ItIsRunning()
    {
        var gated = new GatedGateway(_gateway);
        var schedule = Schedule();
        using var worker = Worker(schedule, Sync(gated));
        await worker.StartAsync(CancellationToken.None);
        var first = await _delay.NextAsync();
        _clock.UtcNow += first.Wait;
        first.Release();
        await gated.Entered.Task.WaitAsync(Patience);

        Assert.Equal((0, 0), (_queue.Depth, _queue.Outstanding));
        Assert.NotNull(schedule.InFlight);
        var exclusive = _queue.TryRunExclusiveAsync("other", _ => Task.FromResult(_gateway.Queries.Count), CancellationToken.None);
        gated.Release.TrySetResult();
        var (ran, queriesThen) = await exclusive.WaitAsync(Patience);

        Assert.True(ran); // not refused because of the sync — it waited behind it
        Assert.Equal(1, queriesThen);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_CompleteInFlightWithTheResultAndClearIt_When_TheSyncEnds()
    {
        var gated = new GatedGateway(_gateway);
        var schedule = Schedule();
        using var worker = Worker(schedule, Sync(gated));
        await worker.StartAsync(CancellationToken.None);
        var first = await _delay.NextAsync();
        _clock.UtcNow += first.Wait;
        first.Release();
        await gated.Entered.Task.WaitAsync(Patience);

        var joined = schedule.InFlight!;
        gated.Release.TrySetResult();
        var result = await joined.WaitAsync(Patience);
        await _delay.NextAsync();

        Assert.NotNull(result);
        Assert.Equal(_lists.Current.Accounts.Count, result.Accounts);
        Assert.Null(schedule.InFlight);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_RecordSkippedAndNotCallQuickBooks_When_QuickBooksIsNotOpen()
    {
        _presence.IsOpen = false;
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);

        var next = await ElapseAsync(await _delay.NextAsync());

        Assert.Empty(_gateway.Requests);
        var last = _lists.LastAttempt!;
        Assert.Equal(
            (ListSyncTrigger.Schedule, ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen),
            (last.Trigger, last.Outcome, last.Reason));
        Assert.Equal(Interval, next.Wait);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_KeepRunningAndWaitAFullInterval_When_TheSyncFails()
    {
        _gateway.QueryThrow = new QuickBooksUnavailableException("simulated: could not open a session");
        Save("a", JobStatus.Posted);
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);

        var next = await ElapseAsync(await _delay.NextAsync());

        Assert.Equal(Interval, next.Wait); // no fast retry
        Assert.Equal(ListSyncOutcome.Failed, _lists.LastAttempt!.Outcome);
        Assert.Equal(ListSyncReasons.QuickBooksUnavailable, _lists.LastAttempt.Reason);
        Assert.Equal(JobStatus.Posted, _store.Get("a")!.Status); // a sync failure never touches a job
        _queue.Enqueue(new JobWorkItem("b", JobAction.Analyse));
        await UntilAsync(() => _processor.Seen.Count == 1); // the loop is still alive
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_WarnOncePerFailureStreak_When_SyncsKeepFailing()
    {
        _gateway.QueryThrow = new QuickBooksUnavailableException("down");
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);

        var pending = await ElapseAsync(await _delay.NextAsync()); // fail 1
        pending = await ElapseAsync(pending); // fail 2
        _gateway.QueryThrow = null;
        pending = await ElapseAsync(pending); // ok
        _gateway.QueryThrow = new QuickBooksUnavailableException("down");
        await ElapseAsync(pending); // fail 3

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Information, LogLevel.Information, LogLevel.Warning],
            SyncLines.Select(l => l.Level));
        Assert.Contains("failed (quickbooks-unavailable): down", SyncLines[0].Message, StringComparison.Ordinal);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_CountSkippedAsNotOk_When_AFailureFollowsASkip()
    {
        _presence.IsOpen = false;
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);

        var pending = await ElapseAsync(await _delay.NextAsync()); // skipped: first not-ok → Warning
        _presence.IsOpen = true;
        _gateway.QueryThrow = new QuickBooksUnavailableException("down");
        await ElapseAsync(pending); // failed: same streak → Information

        Assert.Equal([LogLevel.Warning, LogLevel.Information], SyncLines.Select(l => l.Level));
        Assert.Contains("skipped (quickbooks-not-open)", SyncLines[0].Message, StringComparison.Ordinal);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_MoveTheDueTime_When_AnotherTriggerSyncsMeanwhile()
    {
        using var worker = Worker(Schedule());
        await worker.StartAsync(CancellationToken.None);
        var first = await _delay.NextAsync();

        _clock.UtcNow = Start.AddMinutes(1);
        await Sync().SyncAsync(ListSyncTrigger.Api, "tester", CancellationToken.None); // e.g. the v1 route
        var next = await ElapseAsync(first);

        Assert.Single(_gateway.Queries); // only the API's sync
        Assert.Equal(Start.AddMinutes(1) + Interval - _clock.UtcNow, next.Wait);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_WaitAFullInterval_When_TheAttemptCannotBeRecorded()
    {
        _presence.IsOpen = false;
        var unrecordable = new UnrecordableLists();
        using var worker = Worker(Schedule(source: unrecordable), Sync(lists: unrecordable));
        await worker.StartAsync(CancellationToken.None);

        var next = await ElapseAsync(await _delay.NextAsync());

        Assert.Equal(1, unrecordable.Attempts);
        Assert.Equal(Interval, next.Wait); // not straight away again, although the source never moved
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_NeverSync_When_TheIntervalIsZero()
    {
        var schedule = Schedule(intervalMinutes: 0);
        using var worker = Worker(schedule);
        await worker.StartAsync(CancellationToken.None);

        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        await UntilAsync(() => _processor.Seen.Count == 1);
        _clock.UtcNow += TimeSpan.FromDays(1);
        _queue.Enqueue(new JobWorkItem("b", JobAction.Analyse));
        await UntilAsync(() => _processor.Seen.Count == 2);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _delay.Calls);
        Assert.Empty(_gateway.Requests);
        Assert.Equal(0, _presence.Calls);
        Assert.Null(schedule.NextSyncUtc);
    }

    [Fact]
    public async Task Should_StopPromptly_When_TheHostStopsDuringTheWait()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = Worker(Schedule(), delay: (wait, ct) =>
        {
            waiting.TrySetResult();
            return Task.Delay(wait, ct); // the real delay: two minutes away
        });
        await worker.StartAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(Patience);

        await worker.StopAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task Should_CancelTheSync_When_TheHostStopsWhileItRuns()
    {
        _gateway.QueryHang = true;
        var schedule = Schedule();
        using var worker = Worker(schedule);
        await worker.StartAsync(CancellationToken.None);
        var first = await _delay.NextAsync();
        _clock.UtcNow += first.Wait;
        first.Release();
        await UntilAsync(() => _gateway.Queries.Count == 1);
        var inFlight = schedule.InFlight!;

        await worker.StopAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.True(inFlight.IsCanceled);
    }

    // --- surface ----------------------------------------------------------------------------------------------

    [Fact]
    public void Should_KeepJobWorkerAsTheOnlyBackgroundService_When_TheHostIsBuilt()
    {
        using var factory = new ApiFactory();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        Assert.Equal(typeof(JobWorker), Assert.Single(hosted.OfType<BackgroundService>()).GetType());
        Assert.Equal(
            [typeof(JobWorker), typeof(InFlightRecovery)],
            hosted.Select(h => h.GetType()).Where(t => t.Namespace!.StartsWith("QbAutopost", StringComparison.Ordinal)));
    }

    // --- helpers ----------------------------------------------------------------------------------------------

    private void Save(string jobId, JobStatus status) =>
        _store.Save(new JobRecord { JobId = jobId, Folder = _dir.Combine(jobId), Status = status });

    /// <summary>The worker's waits, one at a time: a test reads the requested span, moves the clock and releases it.</summary>
    private sealed class ManualDelay
    {
        private readonly Channel<Pending> _calls = Channel.CreateUnbounded<Pending>();
        private int _count;

        public int Calls => Volatile.Read(ref _count);

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            _calls.Writer.TryWrite(new Pending(wait, done));
            return done.Task;
        }

        /// <summary>The next wait the worker starts that is still pending (waits cancelled by an item are passed over).</summary>
        public async Task<Pending> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            while (true)
            {
                var pending = await _calls.Reader.ReadAsync(timeout.Token);
                if (!pending.Done.Task.IsCompleted)
                {
                    return pending;
                }
            }
        }

        public sealed record Pending(TimeSpan Wait, TaskCompletionSource Done)
        {
            public void Release() => Done.TrySetResult();
        }
    }

    /// <summary>Holds the list query until the test releases it, so a test can look at the worker mid-sync.</summary>
    private sealed class GatedGateway(IQbGateway inner) : IQbGateway
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await inner.ProcessAsync(qbxml, ct);
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => inner.CurrentCompanyFileAsync(ct);
    }

    /// <summary>A database that is down: every record throws and the last attempt never moves.</summary>
    private sealed class UnrecordableLists : IQbListsStore, IQbListsSource
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public QbLists Current => QbLists.Empty;

        public ListSyncRun? LastAttempt => null;

        public Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("database down");
        }

        public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Task.FromResult<ListSyncRun?>(null);
    }

    private sealed class RecordingProcessor : IJobProcessor
    {
        private readonly object _gate = new();
        private readonly List<string> _seen = [];

        public Action? OnProcess { get; set; }

        public int? QueriesSeen { get; set; }

        public IReadOnlyList<string> Seen
        {
            get
            {
                lock (_gate)
                {
                    return _seen.ToList();
                }
            }
        }

        /// <summary>Awaited before the item is recorded: a test can hold one job on the worker.</summary>
        public Func<JobWorkItem, Task>? Hold { get; set; }

        public async Task ProcessAsync(JobWorkItem item, CancellationToken ct)
        {
            if (Hold is { } hold)
            {
                await hold(item);
            }

            OnProcess?.Invoke();
            lock (_gate)
            {
                _seen.Add($"{item.JobId}:{item.Action}");
            }
        }
    }
}
