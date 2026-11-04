using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
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
        Lists,
        Lists,
        new CompanyCatalog(id => id == 2 ? TakomaLists : Lists));

    private FileQbLists TakomaLists => _takomaLists ??= new FileQbLists(Path.Combine(_dir.Root, "companies", "2", "qb-lists.json"));

    private FileQbLists? _takomaLists;

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

    private const string EmptyAnswer =
        "<QBXML><QBXMLMsgsRs>"
        + "<AccountQueryRs statusCode=\"1\" statusMessage=\"none\" />"
        + "<VendorQueryRs statusCode=\"1\" statusMessage=\"none\" />"
        + "<CustomerQueryRs statusCode=\"1\" statusMessage=\"none\" />"
        + "</QBXMLMsgsRs></QBXML>";

    [Fact]
    public async Task Should_KeepListsAndRecordFailed_When_AnEmptyAnswerWouldWipeThem()
    {
        await Run(Sync());
        _gateway.Response = EmptyAnswer;

        await Assert.ThrowsAsync<QbStatusException>(() => Run(Sync()));

        Assert.Equal(3, Lists.Current.Accounts.Count);
        var run = Lists.LastAttempt!;
        Assert.Equal((ListSyncOutcome.Failed, ListSyncReasons.QbStatus), (run.Outcome, run.Reason));
    }

    [Fact]
    public async Task Should_RecordOk_When_AnEmptyAnswerReplacesNothing()
    {
        _gateway.Response = EmptyAnswer;

        var result = await Run(Sync());

        Assert.Equal((0, 0, 0), (result.Accounts, result.Vendors, result.Customers));
        Assert.Equal(ListSyncOutcome.Ok, Lists.LastAttempt!.Outcome);
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
    [InlineData(typeof(QuickBooksCallException), ListSyncReasons.QuickBooksUnavailable)]
    [InlineData(typeof(FormatException), ListSyncReasons.QbStatus)]
    [InlineData(typeof(System.Xml.XmlException), ListSyncReasons.QbStatus)]
    [InlineData(typeof(InvalidOperationException), null)]
    public async Task Should_MapReason_When_GatewayThrows(Type type, string? reason)
    {
        _gateway.Failure = type == typeof(QuickBooksUnavailableException) ? new QuickBooksUnavailableException("boom")
            : type == typeof(QuickBooksBusyException) ? new QuickBooksBusyException("boom")
            : type == typeof(QuickBooksCallException) ? new QuickBooksCallException("boom", 1)
            : type == typeof(FormatException) ? new FormatException("boom")
            : type == typeof(System.Xml.XmlException) ? new System.Xml.XmlException("boom")
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

    // ---- T-1708: a company context (M17 spec 4.7) ---------------------------------------------------------------

    private const string TakomaFile = @"C:\fake\Takoma.QBW";

    /// <summary>Takoma's own folder: rules that name "Takoma Supplies" where the sample names "Office Supplies".</summary>
    private CompanyContext Takoma()
    {
        var folder = Path.Combine(_dir.Root, "companies", "2");
        Directory.CreateDirectory(folder);
        var rules = Path.Combine(folder, "rules.json");
        File.WriteAllText(
            rules,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "rules.json")).Replace("Office Supplies", "Takoma Supplies"));
        return new CompanyContext(2, "Takoma Park Grocery Store Inc", TakomaFile, rules, Path.Combine(folder, "ledger.json"));
    }

    [Fact]
    public async Task Should_LoadTheCompanysRules_When_SyncingAContext()
    {
        var completion = await Sync().SyncWithRunAsync(Takoma(), ListSyncTrigger.Api, null, CancellationToken.None);

        Assert.Contains("Takoma Supplies", completion.Result.MissingInRules);
        Assert.DoesNotContain("Office Supplies", completion.Result.MissingInRules);
    }

    [Fact]
    public async Task Should_SendToTheCompanysFile_When_SyncingAContext()
    {
        await Sync().SyncWithRunAsync(Takoma(), ListSyncTrigger.Api, null, CancellationToken.None);

        Assert.Equal([TakomaFile], _gateway.Targets);
    }

    [Fact]
    public async Task Should_KeepAuditCopiesInTheCompanysFolder_When_SyncingAContext()
    {
        var takoma = Takoma();

        await Sync().SyncWithRunAsync(takoma, ListSyncTrigger.Api, null, CancellationToken.None);

        var audit = Path.Combine(Path.GetDirectoryName(takoma.LedgerFile)!, QbListSync.AuditDir);
        Assert.True(File.Exists(Path.Combine(audit, "sync-lists.request.qbxml")));
    }

    [Fact]
    public async Task Should_RecordSkippedOpenElsewhere_When_SyncFindsAnotherFileOpen()
    {
        _gateway.Failure = new QuickBooksWrongCompanyException(TakomaFile, @"C:\fake\SA.QBW", "another file is open");

        await Assert.ThrowsAsync<QuickBooksWrongCompanyException>(
            () => Sync().SyncWithRunAsync(Takoma(), ListSyncTrigger.Schedule, null, CancellationToken.None));

        var run = Assert.IsType<ListSyncRun>(TakomaLists.LastAttempt);
        Assert.Equal((ListSyncOutcome.Skipped, ListSyncReasons.CompanyFileOpenElsewhere), (run.Outcome, run.Reason));
    }

    [Fact]
    public async Task Should_RecordSkippedUnavailable_When_CompanyFileCannotBeOpened()
    {
        _gateway.Failure = new QuickBooksCompanyFileUnavailableException(TakomaFile, "0x80040408: could not open");

        await Assert.ThrowsAsync<QuickBooksCompanyFileUnavailableException>(
            () => Sync().SyncWithRunAsync(Takoma(), ListSyncTrigger.Schedule, null, CancellationToken.None));

        var run = Assert.IsType<ListSyncRun>(TakomaLists.LastAttempt);
        Assert.Equal((ListSyncOutcome.Skipped, ListSyncReasons.CompanyFileUnavailable), (run.Outcome, run.Reason));
    }

    [Fact]
    public async Task Should_RecordIntoTheCompanysStore_When_SyncingAContext()
    {
        await Sync().SyncWithRunAsync(Takoma(), ListSyncTrigger.Api, null, CancellationToken.None);

        Assert.Equal(ListSyncOutcome.Ok, TakomaLists.LastAttempt?.Outcome);
        Assert.NotEmpty(TakomaLists.Current.Accounts);
        Assert.Null(Lists.LastAttempt);
        Assert.False(File.Exists(ListsFile));
    }

    [Fact]
    public async Task Should_RefuseWithoutASession_When_CompanyCannotOpenSession()
    {
        var takoma = Takoma() with { Access = CompanyAccess.NeedsApproval };

        var ex = await Assert.ThrowsAsync<CompanyNotReadyException>(
            () => Sync().SyncWithRunAsync(takoma, ListSyncTrigger.Api, null, CancellationToken.None));

        Assert.Same(takoma, ex.Company);
        Assert.Empty(_gateway.Requests);
        Assert.Null(TakomaLists.LastAttempt);
    }

    /// <summary>T-1709: the default company (0 here) is <see cref="Lists"/>; Takoma (2) has its own lists.</summary>
    private sealed class CompanyCatalog(Func<int, FileQbLists> listsOf) : IQbListsCatalog
    {
        public IQbListsSource SourceFor(int companyId) => listsOf(companyId);

        public IQbListsStore StoreFor(int companyId) => listsOf(companyId);
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

        /// <summary>The target file of each targeted request.</summary>
        public List<string> Targets { get; } = [];

        public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct)
        {
            Targets.Add(target.CompanyFile);
            return ProcessAsync(qbxml, ct);
        }

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
