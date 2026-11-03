using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Jobs;

/// <summary>
/// T-1709 (M17 spec 4.7, A-12): with several companies the single <see cref="JobWorker"/>'s scheduled attempt sweeps
/// the selected companies one at a time; a company whose file cannot be opened gets a skipped row and the sweep goes on.
/// </summary>
public sealed class ScheduledCompanySweepTests : IDisposable
{
    private const int DefaultId = 1;
    private const int TakomaId = 2;
    private const int SaId = 3;
    private const int PendingApprovalId = 4;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\fake" : "/fake";
    private static readonly string TakomaFile = Path.Combine(Root, "Takoma.QBW");
    private static readonly string SaFile = Path.Combine(Root, "SA.QBW");
    private static readonly string PendingApprovalFile = Path.Combine(Root, "New.QBW");

    private readonly TempDir _dir = new();
    private readonly JobQueue _queue = new();
    private readonly FixedClock _clock = new();
    private readonly FakeQbGateway _gateway = new();
    private readonly FakeQbPresence _presence = new();
    private readonly FakeCompanyStore _companies = new();
    private readonly ListLogger<JobWorker> _log = new();
    private readonly AppSettings _settings;
    private readonly CompanyResolver _resolver;
    private readonly FileQbLists _defaultLists;
    private readonly FakeQbListsCatalog _catalog;

    public ScheduledCompanySweepTests()
    {
        _settings = new AppSettings
        {
            Company = { Name = ApiFactory.Company, FilePath = FakeQbGateway.DefaultCompanyFile, RulesFile = Fixtures.SampleRules },
            Paths = { Ledger = _dir.Combine("ledger.json"), JobIndex = _dir.Combine("jobs.json") },
            Companies =
            {
                RootFolder = Root,
                DataFolder = _dir.Combine("companies"),
                RulesTemplate = Fixtures.SampleRules,
                ScheduledSync = ScheduledSyncModes.All,
            },
        };
        var options = Options.Create(_settings);
        var folders = new CompanyDataFolder(options);
        _companies.Add(CompanyOf(DefaultId, FakeQbGateway.DefaultCompanyFile, CompanyAccess.Approved) with { IsDefault = true });
        _companies.Add(CompanyOf(TakomaId, TakomaFile, CompanyAccess.Approved));
        _companies.Add(CompanyOf(SaId, SaFile, CompanyAccess.Approved));
        _companies.Add(CompanyOf(PendingApprovalId, PendingApprovalFile, CompanyAccess.NeedsApproval));
        foreach (var id in new[] { TakomaId, SaId, PendingApprovalId })
        {
            folders.Prepare(id);
        }

        _resolver = new CompanyResolver(options, folders, _companies);
        _resolver.UseDefaultId(DefaultId);
        _defaultLists = new FileQbLists(_dir.Combine("qb-lists.json"));
        _catalog = new FakeQbListsCatalog(_defaultLists, _resolver);
    }

    public void Dispose() => _dir.Dispose();

    private static Company CompanyOf(int id, string file, CompanyAccess access) =>
        new(id, null, null, $"Company {id}", file, [file], CompanyState.Active, access, T0, null, null, null, [], false, T0, T0, 1);

    /// <summary>Runs one scheduled attempt (due at once) and returns when the worker waits for the next one.</summary>
    private async Task SweepOnceAsync(IQbGateway? gateway = null, IJobProcessor? processor = null)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = Worker(out _, gateway, processor, (_, ct) =>
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        });

        await worker.StartAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);
    }

    /// <summary>A worker over the companies above; the schedule is due at once (or off, as the sweep says).</summary>
    private JobWorker Worker(
        out ListSyncSchedule schedule, IQbGateway? gateway, IJobProcessor? processor, Func<TimeSpan, CancellationToken, Task> delay)
    {
        var sweep = new CompanySweepSource(_resolver, _catalog, Options.Create(_settings), new FakeSavedRequestStore(), _companies);
        schedule = new ListSyncSchedule(
            new ListSyncSettings { IntervalMinutes = 30, FirstDelayMinutes = 0 }, _defaultLists, _clock, () => sweep.Off);
        var sync = new QbListSync(
            new PipelineOptions
            {
                CompanyName = ApiFactory.Company,
                RulesFile = Fixtures.SampleRules,
                LedgerFile = _settings.Paths.Ledger,
                QbListsFile = _dir.Combine("qb-lists.json"),
            },
            gateway ?? _gateway,
            _clock,
            _defaultLists,
            _defaultLists,
            _catalog);
        var store = new JobStore(Options.Create(_settings), _clock, NullLogger<JobStore>.Instance);
        return new JobWorker(_queue, processor ?? new IdleProcessor(), store, _log, schedule, sync, _presence, delay, sweep);
    }

    private ListSyncRun? LastOf(int companyId) => _catalog.SourceFor(companyId).LastAttempt;

    [Fact]
    public async Task Should_SkipOnlyThatCompany_When_ItsFileIsOpenElsewhere()
    {
        await SweepOnceAsync(new RefusingGateway(_gateway, TakomaFile, SaFile));

        var takoma = LastOf(TakomaId)!;
        Assert.Equal((ListSyncTrigger.Schedule, ListSyncOutcome.Skipped, ListSyncReasons.CompanyFileOpenElsewhere), (takoma.Trigger, takoma.Outcome, takoma.Reason));
        Assert.Equal(ListSyncOutcome.Ok, LastOf(DefaultId)!.Outcome);
        Assert.Equal(ListSyncOutcome.Ok, LastOf(SaId)!.Outcome);
    }

    [Fact]
    public async Task Should_SkipAsUnavailable_When_ItsFileCannotBeOpened()
    {
        _gateway.UnavailableCompanyFile = TakomaFile;

        await SweepOnceAsync();

        var takoma = LastOf(TakomaId)!;
        Assert.Equal((ListSyncOutcome.Skipped, ListSyncReasons.CompanyFileUnavailable), (takoma.Outcome, takoma.Reason));
        Assert.Equal(ListSyncOutcome.Ok, LastOf(SaId)!.Outcome);
    }

    [Fact]
    public async Task Should_SyncEachSelectedCompanyFromItsOwnFile_When_Due()
    {
        await SweepOnceAsync();

        Assert.Equal([null, TakomaFile, SaFile], _gateway.Targets);
    }

    [Fact]
    public async Task Should_NeverOpenTheFile_When_CompanyNeedsApproval()
    {
        await SweepOnceAsync();

        Assert.DoesNotContain(PendingApprovalFile, _gateway.Targets);
        Assert.Null(LastOf(PendingApprovalId));
    }

    [Fact]
    public async Task Should_SkipEveryCompanyAsNotOpen_When_QuickBooksIsNotRunning()
    {
        _presence.IsOpen = false;

        await SweepOnceAsync();

        Assert.Empty(_gateway.Requests);
        Assert.Equal(1, _presence.Calls);
        foreach (var id in new[] { DefaultId, TakomaId, SaId })
        {
            var run = LastOf(id)!;
            Assert.Equal((ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen), (run.Outcome, run.Reason));
        }

        Assert.Null(LastOf(PendingApprovalId));
    }

    [Fact]
    public async Task Should_SyncOnlyWaitingOrNeverSynced_When_ModeIsWaitingOnly()
    {
        _settings.Companies.ScheduledSync = ScheduledSyncModes.WaitingOnly;
        await _catalog.StoreFor(DefaultId).RecordAsync(
            new ListSyncRun { Trigger = ListSyncTrigger.Api, StartedUtc = _clock.UtcNow.AddDays(-1), FinishedUtc = _clock.UtcNow.AddDays(-1), Outcome = ListSyncOutcome.Failed },
            null,
            CancellationToken.None);

        await SweepOnceAsync();

        Assert.Equal([TakomaFile, SaFile], _gateway.Targets); // the default company has synced before and has nothing waiting
    }

    // ---- review fix round 1 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_RunAQueuedItemBeforeTheNextCompany_When_ItArrivesDuringTheSweep()
    {
        var targetsWhenItemRan = -1;
        var processor = new IdleProcessor { OnProcess = () => targetsWhenItemRan = _gateway.Targets.Count };
        var gateway = new HookGateway(_gateway, () => _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse)));

        await SweepOnceAsync(gateway, processor);

        Assert.Equal(1, targetsWhenItemRan); // after the first company, before the second
        Assert.Equal([null, TakomaFile, SaFile], _gateway.Targets);
        Assert.Equal(0, _queue.Outstanding);
    }

    [Fact]
    public async Task Should_NeverSyncOrScheduleASync_When_ScheduledSyncIsOff()
    {
        _settings.Companies.ScheduledSync = ScheduledSyncModes.Off;
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0;
        using var worker = Worker(
            out var schedule,
            null,
            new IdleProcessor { OnProcess = () => processed.TrySetResult() },
            (_, ct) =>
            {
                Interlocked.Increment(ref delays);
                return Task.Delay(Timeout.Infinite, ct);
            });

        await worker.StartAsync(CancellationToken.None);
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        await processed.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(_gateway.Requests);
        Assert.Null(schedule.NextSyncUtc);
        Assert.Equal(0, delays);
        Assert.Null(LastOf(DefaultId));
    }

    /// <summary>Runs <paramref name="onFirstCall"/> once, at the first call (the default company's sync), then forwards.</summary>
    private sealed class HookGateway(IQbGateway inner, Action onFirstCall) : IQbGateway
    {
        private int _calls;

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Hook();
            return inner.ProcessAsync(qbxml, ct);
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => inner.CurrentCompanyFileAsync(ct);

        public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct)
        {
            Hook();
            return inner.ProcessAsync(target, qbxml, ct);
        }

        public Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) => inner.CurrentCompanyFileAsync(target, ct);

        private void Hook()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                onFirstCall();
            }
        }
    }

    /// <summary>QuickBooks has <paramref name="openFile"/> open, so a session on <paramref name="refusedFile"/> is refused.</summary>
    private sealed class RefusingGateway(IQbGateway inner, string refusedFile, string openFile) : IQbGateway
    {
        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => inner.ProcessAsync(qbxml, ct);

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => inner.CurrentCompanyFileAsync(ct);

        public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) =>
            string.Equals(target.CompanyFile, refusedFile, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException<string>(new QuickBooksWrongCompanyException(refusedFile, openFile, "simulated: another file is open"))
                : inner.ProcessAsync(target, qbxml, ct);

        public Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) => inner.CurrentCompanyFileAsync(target, ct);
    }

    private sealed class IdleProcessor : IJobProcessor
    {
        public Action? OnProcess { get; init; }

        public Task ProcessAsync(JobWorkItem item, CancellationToken ct)
        {
            OnProcess?.Invoke();
            return Task.CompletedTask;
        }
    }
}
