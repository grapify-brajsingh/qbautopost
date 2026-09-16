using System.Xml.Linq;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>FR-13 undo against the ledger.</summary>
public sealed class BatchUndoTests : IDisposable
{
    private const string Batch1 = "2026-08-tropicana#1";
    private const string Batch2 = "2026-08-tropicana#2";

    private readonly TempJobFolder _dir = new();
    private readonly DeleteGateway _gateway = new();

    public BatchUndoTests()
    {
        Store.Save(new Ledger
        {
            Jobs =
            [
                new LedgerJob { JobId = "2026-08-tropicana", BatchId = Batch1, Posted = 2, TxnIds = ["T1", "T2"] },
                new LedgerJob { JobId = "2026-08-tropicana", BatchId = Batch2, Posted = 1, TxnIds = ["T3"] },
                new LedgerJob { JobId = "2026-09-other", BatchId = "2026-09-other#1", Posted = 0 },
            ],
            Posted = [Entry(Batch1, "T1", TxnKind.Check), Entry(Batch1, "T2", TxnKind.Deposit), Entry(Batch2, "T3", TxnKind.CcCharge)],
        });
    }

    public void Dispose() => _dir.Dispose();

    private LedgerStore Store => new(Path.Combine(_dir.Root, "ledger.json"));

    private string AuditDir => Path.Combine(_dir.Folder, "output");

    private static LedgerEntry Entry(string batch, string txnId, TxnKind kind) => new()
    {
        BatchId = batch,
        JobId = "2026-08-tropicana",
        Fingerprint = "fp-" + txnId,
        TxnId = txnId,
        Kind = kind,
        Account = "Chase Checking 4521",
        Amount = 10m,
        Date = new DateOnly(2026, 8, 1),
        SourceFile = "a.csv",
    };

    private BatchUndo Undo() => new(
        new PipelineOptions
        {
            CompanyName = "Tropicana Properties LLC",
            RulesFile = "unused",
            LedgerFile = Path.Combine(_dir.Root, "ledger.json"),
            QbListsFile = "unused",
        },
        _gateway);

    [Fact]
    public async Task Should_ReturnNotFound_When_BatchIsNotInLedger()
    {
        var result = await Undo().UndoAsync("nope#1", AuditDir, CancellationToken.None);

        Assert.False(result.Found);
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task Should_DeleteOnlyTheBatchEntries_When_Undoing()
    {
        await Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None);

        var sent = XDocument.Parse(Assert.Single(_gateway.Requests)).Descendants("TxnDelRq")
            .Select(r => (r.Element("TxnDelType")!.Value, r.Element("TxnID")!.Value));
        Assert.Equal([("Check", "T1"), ("Deposit", "T2")], sent);
    }

    [Fact]
    public async Task Should_MarkEntriesAndBatchUndone_When_AllDeletesSucceed()
    {
        var result = await Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None);

        Assert.Equal((true, "2026-08-tropicana", 2, true), (result.Found, result.JobId, result.Deleted, result.BatchUndone));
        Assert.Empty(result.Failed);
        var ledger = Store.Load();
        Assert.Equal([true, true, false], ledger.Posted.Select(p => p.Undone));
        Assert.Equal([true, false, false], ledger.Jobs.Select(j => j.Undone));
        Assert.False(ledger.IsPosted("fp-T1"));
    }

    [Fact]
    public async Task Should_ReportFailureAndKeepBatchLive_When_ADeleteIsRefused()
    {
        _gateway.Refuse.Add("T2");

        var result = await Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("T2", failure.TxnId);
        Assert.StartsWith("3120: ", failure.Message, StringComparison.Ordinal);
        Assert.False(result.BatchUndone);
        var ledger = Store.Load();
        Assert.Equal([true, false, false], ledger.Posted.Select(p => p.Undone));
        Assert.False(ledger.Jobs[0].Undone);
    }

    [Fact]
    public async Task Should_SendOnlyRemainingEntries_When_UndoIsRepeated()
    {
        _gateway.Refuse.Add("T2");
        await Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None);
        _gateway.Refuse.Clear();

        var result = await Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None);

        Assert.Contains("<TxnID>T2</TxnID>", _gateway.Requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("<TxnID>T1</TxnID>", _gateway.Requests[1], StringComparison.Ordinal);
        Assert.True(result.BatchUndone);
    }

    [Fact]
    public async Task Should_NotCallQuickBooks_When_BatchHasNoLiveEntry()
    {
        var result = await Undo().UndoAsync("2026-09-OTHER#1", AuditDir, CancellationToken.None);

        Assert.Equal((true, 0, false), (result.Found, result.Deleted, result.BatchUndone));
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task Should_WriteUndoAuditFiles_When_Undoing()
    {
        await Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(AuditDir, "undo-2026-08-tropicana-1.request.qbxml")));
        Assert.True(File.Exists(Path.Combine(AuditDir, "undo-2026-08-tropicana-1.response.qbxml")));
    }

    [Fact]
    public async Task Should_MarkNothing_When_QuickBooksCallFails()
    {
        _gateway.Throw = new QuickBooksBusyException("slow");

        await Assert.ThrowsAsync<QuickBooksBusyException>(() => Undo().UndoAsync(Batch1, AuditDir, CancellationToken.None));

        Assert.All(Store.Load().Posted, p => Assert.False(p.Undone));
    }

    private sealed class DeleteGateway : IQbGateway
    {
        public List<string> Requests { get; } = [];

        public HashSet<string> Refuse { get; } = [];

        public Exception? Throw { get; set; }

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Requests.Add(qbxml);
            if (Throw is not null)
            {
                throw Throw;
            }

            var answers = XDocument.Parse(qbxml).Descendants("TxnDelRq").Select(rq =>
            {
                var id = rq.Element("TxnID")!.Value;
                return Refuse.Contains(id)
                    ? new XElement("TxnDelRs", new XAttribute("requestID", rq.Attribute("requestID")!.Value), new XAttribute("statusCode", 3120), new XAttribute("statusMessage", "not found"))
                    : new XElement("TxnDelRs", new XAttribute("requestID", rq.Attribute("requestID")!.Value), new XAttribute("statusCode", 0), new XElement("TxnID", id));
            });
            return Task.FromResult(new XElement("QBXML", new XElement("QBXMLMsgsRs", answers)).ToString());
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult("");
    }
}
