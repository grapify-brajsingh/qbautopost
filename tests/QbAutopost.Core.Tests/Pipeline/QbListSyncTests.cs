using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>FR-15: sync writes qb-lists.json and reports rules accounts QuickBooks lacks.</summary>
public sealed class QbListSyncTests : IDisposable
{
    private readonly TempJobFolder _dir = new();
    private readonly RecordingGateway _gateway = new() { Response = Fixtures.Read("qbxml", "list-response.xml") };

    public void Dispose() => _dir.Dispose();

    private string ListsFile => Path.Combine(_dir.Root, "data", "qb-lists.json");

    private FileQbLists Lists => _lists ??= new FileQbLists(ListsFile);

    private FileQbLists? _lists;

    private QbListSync Sync(string? rulesFile = null) => new(
        new PipelineOptions
        {
            CompanyName = "Tropicana Properties LLC",
            RulesFile = rulesFile ?? Path.Combine(AppContext.BaseDirectory, "samples", "rules.json"),
            LedgerFile = Path.Combine(_dir.Root, "data", "ledger.json"),
            QbListsFile = ListsFile,
        },
        _gateway,
        new FixedTime(),
        Lists);

    private static Task<ListSyncResult> Run(QbListSync sync, string? by = null) =>
        sync.SyncAsync(ListSyncTrigger.Api, by, CancellationToken.None);

    [Fact]
    public async Task Should_WriteListsWithSyncTime_When_QuickBooksAnswers()
    {
        await Sync().SyncAsync(ListSyncTrigger.Api, null, CancellationToken.None);

        var lists = new QbListsStore(ListsFile).Load();
        Assert.Equal(3, lists.Accounts.Count);
        Assert.Equal(2, lists.Vendors.Count);
        Assert.Equal(FixedTime.Now, lists.SyncedUtc);
    }

    [Fact]
    public async Task Should_ReturnCounts_When_QuickBooksAnswers()
    {
        var result = await Sync().SyncAsync(ListSyncTrigger.Api, null, CancellationToken.None);

        Assert.Equal((3, 2, 0), (result.Accounts, result.Vendors, result.Customers));
    }

    [Fact]
    public async Task Should_ListRulesAccountsQuickBooksLacks_When_Synced()
    {
        var result = await Sync().SyncAsync(ListSyncTrigger.Api, null, CancellationToken.None);

        Assert.Equal(
            ["Chase Sapphire 7788", "Office Supplies", "Utilities", "Automobile Expense", "Ask My Accountant", "Rental Income"],
            result.MissingInRules);
    }

    [Fact]
    public async Task Should_SendTheListQueryAndKeepAuditCopies_When_Synced()
    {
        var sync = Sync();

        await sync.SyncAsync(ListSyncTrigger.Api, null, CancellationToken.None);

        Assert.Equal(QbListQuery.Build(), Assert.Single(_gateway.Requests));
        Assert.True(File.Exists(Path.Combine(sync.AuditFolder, "sync-lists.request.qbxml")));
        Assert.Equal(_gateway.Response, File.ReadAllText(Path.Combine(sync.AuditFolder, "sync-lists.response.qbxml")));
    }

    [Fact]
    public async Task Should_KeepOldLists_When_QueryIsRefused()
    {
        new QbListsStore(ListsFile).Save(new Core.Models.QbLists { Vendors = ["Old Vendor"] });
        _gateway.Response = "<QBXML><QBXMLMsgsRs><AccountQueryRs statusCode=\"500\" statusMessage=\"x\" /></QBXMLMsgsRs></QBXML>";

        await Assert.ThrowsAsync<QbStatusException>(() => Sync().SyncAsync(ListSyncTrigger.Api, null, CancellationToken.None));

        Assert.Equal(["Old Vendor"], new QbListsStore(ListsFile).Load().Vendors);
    }

    [Fact]
    public async Task Should_NotCallQuickBooks_When_RulesCannotBeRead()
    {
        var badRules = Path.Combine(_dir.Root, "rules.json");
        File.WriteAllText(badRules, "{ not json");

        await Assert.ThrowsAnyAsync<Exception>(() => Sync(badRules).SyncAsync(ListSyncTrigger.Api, null, CancellationToken.None));

        Assert.Empty(_gateway.Requests);
        Assert.False(File.Exists(ListsFile));
    }

    [Fact]
    public async Task Should_RecordOkRun_When_QuickBooksAnswers()
    {
        await Run(Sync(), "ops-client");

        var run = Assert.IsType<ListSyncRun>(await Lists.LastAttemptAsync(CancellationToken.None));
        Assert.Equal(ListSyncOutcome.Ok, run.Outcome);
        Assert.Equal(ListSyncTrigger.Api, run.Trigger);
        Assert.Equal("ops-client", run.RequestedBy);
        Assert.Equal((3, 2, 0), (run.Accounts, run.Vendors, run.Customers));
        Assert.Equal(6, run.MissingInRules!.Count);
        Assert.Equal(FixedTime.Now, run.StartedUtc);
        Assert.Equal(3, Lists.Current.Accounts.Count);
    }

    [Fact]
    public async Task Should_RecordFailedRunAndRethrow_When_QueryIsRefused()
    {
        await Lists.RecordAsync(OkRun(), new QbLists { Vendors = ["Old Vendor"] }, CancellationToken.None);
        _gateway.Response = "<QBXML><QBXMLMsgsRs><AccountQueryRs statusCode=\"500\" statusMessage=\"x\" /></QBXMLMsgsRs></QBXML>";

        await Assert.ThrowsAsync<QbStatusException>(() => Run(Sync()));

        var run = Assert.IsType<ListSyncRun>(Lists.LastAttempt);
        Assert.Equal((ListSyncOutcome.Failed, ListSyncReasons.QbStatus), (run.Outcome, run.Reason));
        Assert.False(string.IsNullOrEmpty(run.Error));
        Assert.Null(run.Accounts);
        Assert.Equal(["Old Vendor"], Lists.Current.Vendors);
    }

    [Theory]
    [InlineData(typeof(QuickBooksBusyException), ListSyncReasons.QuickBooksBusy)]
    [InlineData(typeof(QuickBooksUnavailableException), ListSyncReasons.QuickBooksUnavailable)]
    [InlineData(typeof(InvalidOperationException), null)]
    public async Task Should_MapReason_When_GatewayThrows(Type type, string? reason)
    {
        _gateway.Failure = type == typeof(QuickBooksUnavailableException) ? new QuickBooksUnavailableException("boom")
            : type == typeof(QuickBooksBusyException) ? new QuickBooksBusyException("boom")
            : new InvalidOperationException("boom");

        await Assert.ThrowsAsync(type, () => Run(Sync()));

        var run = Assert.IsType<ListSyncRun>(Lists.LastAttempt);
        Assert.Equal(reason, run.Reason);
        Assert.Equal("boom", run.Error);
    }

    [Fact]
    public async Task Should_RecordRulesUnreadable_When_RulesCannotBeRead()
    {
        var badRules = Path.Combine(_dir.Root, "rules.json");
        File.WriteAllText(badRules, "{ not json");

        await Assert.ThrowsAnyAsync<Exception>(() => Run(Sync(badRules)));

        var run = Assert.IsType<ListSyncRun>(Lists.LastAttempt);
        Assert.Equal((ListSyncOutcome.Failed, ListSyncReasons.RulesUnreadable), (run.Outcome, run.Reason));
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task Should_RecordSkippedRunWithoutCallingQuickBooks_When_Skipped()
    {
        await Sync().RecordSkippedAsync(ListSyncTrigger.Schedule, null, ListSyncReasons.QuickBooksNotOpen, CancellationToken.None);

        var run = Assert.IsType<ListSyncRun>(Lists.LastAttempt);
        Assert.Equal((ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen, ListSyncTrigger.Schedule), (run.Outcome, run.Reason, run.Trigger));
        Assert.Equal(run.StartedUtc, run.FinishedUtc);
        Assert.Empty(_gateway.Requests);
        Assert.False(File.Exists(ListsFile));
    }

    [Fact]
    public async Task Should_NotRecord_When_Cancelled()
    {
        _gateway.Failure = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sync().SyncAsync(ListSyncTrigger.Api, null, cts.Token));

        Assert.Null(Lists.LastAttempt);
    }

    [Fact]
    public void Should_TruncateError_When_OverOneThousandChars()
    {
        var run = OkRun() with { Error = new string('x', 1500) };

        Assert.Equal(1000, run.Error!.Length);
    }

    [Fact]
    public async Task Should_NeverChangeFile_When_RunIsNotOk()
    {
        await Lists.RecordAsync(OkRun(), new QbLists { Vendors = ["Kept"] }, CancellationToken.None);
        var before = File.ReadAllText(ListsFile);

        await Lists.RecordAsync(OkRun() with { Outcome = ListSyncOutcome.Failed, Reason = "qb-status" }, new QbLists { Vendors = ["Other"] }, CancellationToken.None);
        await Lists.RecordAsync(OkRun() with { Outcome = ListSyncOutcome.Skipped }, null, CancellationToken.None);

        Assert.Equal(before, File.ReadAllText(ListsFile));
        Assert.Equal(["Kept"], Lists.Current.Vendors);
    }

    [Fact]
    public async Task Should_TrackEveryRun_When_RecordedThroughFileLists()
    {
        Assert.Null(await Lists.LastAttemptAsync(CancellationToken.None));

        await Lists.RecordAsync(OkRun(), new QbLists(), CancellationToken.None);
        await Lists.RecordAsync(OkRun() with { Outcome = ListSyncOutcome.Skipped, Reason = "worker-busy" }, null, CancellationToken.None);

        Assert.Equal(ListSyncOutcome.Skipped, Lists.LastAttempt!.Outcome);
        Assert.Same(Lists.LastAttempt, await Lists.LastAttemptAsync(CancellationToken.None));
    }

    [Fact]
    public void Should_ReturnEmptyLists_When_NothingSynced()
    {
        Assert.Null(Lists.Current.SyncedUtc);
        Assert.Empty(Lists.Current.Accounts);
    }

    private static ListSyncRun OkRun() => new()
    {
        Trigger = ListSyncTrigger.Api,
        StartedUtc = FixedTime.Now,
        FinishedUtc = FixedTime.Now,
        Outcome = ListSyncOutcome.Ok,
    };

    private sealed class RecordingGateway : IQbGateway
    {
        public string Response { get; set; } = "";

        public Exception? Failure { get; set; }

        public List<string> Requests { get; } = [];

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Requests.Add(qbxml);
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(Response);
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult("");
    }

    private sealed class FixedTime : IClock
    {
        public static readonly DateTime Now = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow => Now;

        public DateOnly Today => DateOnly.FromDateTime(Now);
    }
}
