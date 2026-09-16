using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

public sealed class MapperTierTests
{
    private static readonly QbLists Lists = new() { Vendors = ["Joe's Plumbing"] };

    private static LedgerEntry Posted(string payee, string lineAccount, int n) => new()
    {
        BatchId = "old#1",
        JobId = "old",
        Fingerprint = $"fp{n}",
        TxnId = $"TX{n}",
        Kind = TxnKind.Check,
        Account = "Chase Checking 4521",
        Payee = payee,
        LineAccount = lineAccount,
        Amount = 100m,
        Date = new DateOnly(2026, 7, n),
        SourceFile = "old.csv",
    };

    [Fact]
    public void Should_UseHistoryTier_When_PayeeHasEnoughDominantPostings()
    {
        var history = new[]
        {
            Posted("Joe's Plumbing", "Repairs and Maintenance", 1),
            Posted("Joe's Plumbing", "Repairs and Maintenance", 2),
            Posted("Joe's Plumbing", "Repairs and Maintenance", 3),
        };
        var mapper = new Mapper(Fixtures.SampleRules(), Lists, history);

        var txn = mapper.Map(Lines.Bank(Direction.Debit, 90m, "ACH DEBIT JOES PLUMBING"));

        Assert.Equal("Joe's Plumbing", txn.Payee);
        Assert.Equal("Repairs and Maintenance", txn.LineAccount);
        Assert.Equal(Confidence.History, txn.Confidence);
        Assert.Equal(2, txn.Tier);
    }

    [Fact]
    public void Should_Hold_When_HistoryIsNotDominant()
    {
        var history = new[]
        {
            Posted("Joe's Plumbing", "Repairs and Maintenance", 1),
            Posted("Joe's Plumbing", "Repairs and Maintenance", 2),
            Posted("Joe's Plumbing", "Utilities", 3),
        };
        var mapper = new Mapper(Fixtures.SampleRules(), Lists, history);

        var txn = mapper.Map(Lines.Bank(Direction.Debit, 90m, "ACH DEBIT JOES PLUMBING"));

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.NoAccountRule, txn.Reason);
    }

    [Fact]
    public void Should_IgnoreUndoneEntries_When_CountingHistory()
    {
        var history = new[]
        {
            Posted("Joe's Plumbing", "Repairs and Maintenance", 1),
            Posted("Joe's Plumbing", "Repairs and Maintenance", 2),
            Posted("Joe's Plumbing", "Repairs and Maintenance", 3) with { Undone = true },
        };
        var mapper = new Mapper(Fixtures.SampleRules(), Lists, history);

        var txn = mapper.Map(Lines.Bank(Direction.Debit, 90m, "ACH DEBIT JOES PLUMBING"));

        Assert.Equal(Decision.Hold, txn.Decision);
    }

    [Fact]
    public void Should_PreferVendorRule_When_VendorRuleAndHistoryDisagree()
    {
        var history = Enumerable.Range(1, 5).Select(n => Posted("Home Depot", "Tools", n)).ToList();
        var mapper = new Mapper(Fixtures.SampleRules(), null, history);

        var txn = mapper.Map(Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA"));

        Assert.Equal("Repairs and Maintenance", txn.LineAccount);
        Assert.Equal(1, txn.Tier);
    }
}
