using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Tests.Store;

public sealed class LedgerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qbautopost-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Ledger SampleLedger() => new()
    {
        Jobs = [new LedgerJob { JobId = "2026-08-tropicana", BatchId = "2026-08-tropicana#1", Posted = 1, TxnIds = ["1A2B-1"] }],
        Posted =
        [
            new LedgerEntry
            {
                BatchId = "2026-08-tropicana#1",
                JobId = "2026-08-tropicana",
                Fingerprint = "abc",
                TxnId = "1A2B-1",
                Kind = TxnKind.Check,
                Account = "Chase Checking 4521",
                Payee = "Florida Power & Light",
                LineAccount = "Utilities",
                Amount = 311.40m,
                Date = new DateOnly(2026, 8, 12),
                RefNumber = "ACH",
                Memo = "ACH DEBIT FPL ELECTRIC UTILITY",
                SourceFile = "chase-checking-4521.csv",
                LineNo = 4,
            },
        ],
    };

    [Fact]
    public void Should_ReturnEmptyLedger_When_FileDoesNotExist()
    {
        var ledger = new LedgerStore(Path.Combine(_dir, "ledger.json")).Load();

        Assert.Empty(ledger.Jobs);
        Assert.Empty(ledger.Posted);
    }

    [Fact]
    public void Should_RoundTripEntries_When_SavedAndLoaded()
    {
        var store = new LedgerStore(Path.Combine(_dir, "ledger.json"));

        store.Save(SampleLedger());
        var loaded = store.Load();

        Assert.Equal(SampleLedger().Posted[0], loaded.Posted[0]);
        Assert.True(loaded.HasJob("2026-08-TROPICANA"));
        Assert.True(loaded.IsPosted("abc"));
    }

    [Fact]
    public void Should_LeaveNoTempFile_When_Saved()
    {
        var path = Path.Combine(_dir, "ledger.json");

        new LedgerStore(path).Save(SampleLedger());

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Should_WriteIsoDatesAndStringEnums_When_Saved()
    {
        var path = Path.Combine(_dir, "ledger.json");

        new LedgerStore(path).Save(SampleLedger());
        var json = File.ReadAllText(path);

        Assert.Contains("\"date\": \"2026-08-12\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"Check\"", json, StringComparison.Ordinal);
        Assert.Contains("\"amount\": 311.40", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NotCountUndoneEntry_When_CheckingIsPosted()
    {
        var ledger = SampleLedger() with { Posted = [SampleLedger().Posted[0] with { Undone = true }] };

        Assert.False(ledger.IsPosted("abc"));
    }
}
