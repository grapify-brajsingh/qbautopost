using QbAutopost.Core.Abstractions;
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

    private QbListSync Sync(string? rulesFile = null) => new(
        new PipelineOptions
        {
            CompanyName = "Tropicana Properties LLC",
            RulesFile = rulesFile ?? Path.Combine(AppContext.BaseDirectory, "samples", "rules.json"),
            LedgerFile = Path.Combine(_dir.Root, "data", "ledger.json"),
            QbListsFile = ListsFile,
        },
        _gateway,
        new FixedTime());

    [Fact]
    public async Task Should_WriteListsWithSyncTime_When_QuickBooksAnswers()
    {
        await Sync().SyncAsync(CancellationToken.None);

        var lists = new QbListsStore(ListsFile).Load();
        Assert.Equal(3, lists.Accounts.Count);
        Assert.Equal(2, lists.Vendors.Count);
        Assert.Equal(FixedTime.Now, lists.SyncedUtc);
    }

    [Fact]
    public async Task Should_ReturnCounts_When_QuickBooksAnswers()
    {
        var result = await Sync().SyncAsync(CancellationToken.None);

        Assert.Equal((3, 2, 0), (result.Accounts, result.Vendors, result.Customers));
    }

    [Fact]
    public async Task Should_ListRulesAccountsQuickBooksLacks_When_Synced()
    {
        var result = await Sync().SyncAsync(CancellationToken.None);

        Assert.Equal(
            ["Chase Sapphire 7788", "Office Supplies", "Utilities", "Automobile Expense", "Ask My Accountant", "Rental Income"],
            result.MissingInRules);
    }

    [Fact]
    public async Task Should_SendTheListQueryAndKeepAuditCopies_When_Synced()
    {
        var sync = Sync();

        await sync.SyncAsync(CancellationToken.None);

        Assert.Equal(QbListQuery.Build(), Assert.Single(_gateway.Requests));
        Assert.True(File.Exists(Path.Combine(sync.AuditFolder, "sync-lists.request.qbxml")));
        Assert.Equal(_gateway.Response, File.ReadAllText(Path.Combine(sync.AuditFolder, "sync-lists.response.qbxml")));
    }

    [Fact]
    public async Task Should_KeepOldLists_When_QueryIsRefused()
    {
        new QbListsStore(ListsFile).Save(new Core.Models.QbLists { Vendors = ["Old Vendor"] });
        _gateway.Response = "<QBXML><QBXMLMsgsRs><AccountQueryRs statusCode=\"500\" statusMessage=\"x\" /></QBXMLMsgsRs></QBXML>";

        await Assert.ThrowsAsync<QbStatusException>(() => Sync().SyncAsync(CancellationToken.None));

        Assert.Equal(["Old Vendor"], new QbListsStore(ListsFile).Load().Vendors);
    }

    [Fact]
    public async Task Should_NotCallQuickBooks_When_RulesCannotBeRead()
    {
        var badRules = Path.Combine(_dir.Root, "rules.json");
        File.WriteAllText(badRules, "{ not json");

        await Assert.ThrowsAnyAsync<Exception>(() => Sync(badRules).SyncAsync(CancellationToken.None));

        Assert.Empty(_gateway.Requests);
        Assert.False(File.Exists(ListsFile));
    }

    private sealed class RecordingGateway : IQbGateway
    {
        public string Response { get; set; } = "";

        public List<string> Requests { get; } = [];

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Requests.Add(qbxml);
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
