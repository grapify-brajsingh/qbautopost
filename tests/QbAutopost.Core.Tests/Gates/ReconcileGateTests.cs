using QbAutopost.Core.Extract;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Gates;

public sealed class ReconcileGateTests
{
    private static IReadOnlyList<StatementLine> SampleBankRows() =>
        new CsvStatementParser(Fixtures.SampleRules().CsvLayouts).Parse(Fixtures.SampleBankCsv).Rows;

    [Fact]
    public void Should_Reconcile_When_RowsAreNewestFirst()
    {
        var result = ReconcileGate.CheckBalanceChain(SampleBankRows());

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Verified);
        Assert.Contains("reverse order", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Reconcile_When_RowsAreOldestFirst()
    {
        var oldestFirst = SampleBankRows().Reverse().ToList();

        var result = ReconcileGate.CheckBalanceChain(oldestFirst);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("file order", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Fail_When_OneAmountDoesNotMatchTheBalances()
    {
        var rows = SampleBankRows().ToList();
        rows[1] = rows[1] with { Amount = rows[1].Amount + 1m };

        var result = ReconcileGate.CheckBalanceChain(rows);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Should_Fail_When_DirectionsAreInverted()
    {
        var inverted = SampleBankRows()
            .Select(r => r with { Direction = r.Direction == Direction.Debit ? Direction.Credit : Direction.Debit })
            .ToList();

        var result = ReconcileGate.CheckBalanceChain(inverted);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Should_PassUnverified_When_StatementHasNoBalanceColumn()
    {
        var rows = new[]
        {
            Lines.Card(Direction.Debit, 62.18m, "AMAZON"),
            Lines.Card(Direction.Debit, 48.75m, "SHELL OIL"),
        };

        var result = ReconcileGate.CheckBalanceChain(rows);

        Assert.True(result.Ok);
        Assert.False(result.Verified);
        Assert.Equal(ReconcileResult.NotVerifiable, result.Message);
    }

    [Fact]
    public void Should_Fail_When_BalanceColumnIsOnlyPartlyFilled()
    {
        var rows = new[]
        {
            Lines.Bank(Direction.Debit, 10m, "A", balance: 90m),
            Lines.Bank(Direction.Debit, 10m, "B"),
        };

        var result = ReconcileGate.CheckBalanceChain(rows);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Should_TreatChargesAsRaisingBalance_When_StatementIsACard()
    {
        var rows = new[]
        {
            Lines.Card(Direction.Debit, 100m, "OPEN", lineNo: 1) with { Balance = 100m },
            Lines.Card(Direction.Debit, 50m, "CHARGE", lineNo: 2) with { Balance = 150m },
            Lines.Card(Direction.Credit, 30m, "REFUND", lineNo: 3) with { Balance = 120m },
        };

        var result = ReconcileGate.CheckBalanceChain(rows);

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Verified);
    }
}
