using QbAutopost.Core.Extract;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Gates;

/// <summary>G1 for Hermes T2 output (spec FR-4): opening/closing, row count, period, running balances.</summary>
public sealed class ReconcileGateExtractionTests
{
    private static readonly DateOnly August1 = new(2026, 8, 1);
    private static readonly DateOnly August31 = new(2026, 8, 31);

    /// <summary>The sample bank statement oldest-first, as T2 returns it (13195.87 → 10230.45).</summary>
    private static List<StatementLine> BankRows() =>
        new CsvStatementParser(Fixtures.SampleRules().CsvLayouts).Parse(Fixtures.SampleBankCsv).Rows.Reverse().ToList();

    private static StatementTotals BankTotals(
        decimal? opening = 13195.87m, decimal? closing = 10230.45m, int? count = 7, DateOnly? start = null, DateOnly? end = null) =>
        new(start ?? August1, end ?? August31, opening, closing, count);

    [Fact]
    public void Should_PassVerified_When_OpeningPlusCreditsMinusDebitsIsClosing()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), BankTotals());

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Verified);
    }

    [Fact]
    public void Should_Pass_When_ClosingIsOffByOneCent()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), BankTotals(closing: 10230.46m));

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void Should_Fail_When_ClosingIsOffByTwoCents()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), BankTotals(closing: 10230.47m));

        Assert.False(result.Ok);
        Assert.True(result.Verified);
        Assert.Contains("closing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Fail_When_ARowIsMissing()
    {
        var rows = BankRows();
        rows.RemoveAt(3);

        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, rows, BankTotals(count: null));

        Assert.False(result.Ok);
    }

    [Fact]
    public void Should_UseCardSign_When_StatementIsACard()
    {
        // Card: charges (debits) raise the balance owed, payments (credits) lower it.
        List<StatementLine> rows =
        [
            Lines.Card(Direction.Debit, 62.18m, "AMAZON", "2026-08-03"),
            Lines.Card(Direction.Debit, 48.75m, "SHELL", "2026-08-09"),
            Lines.Card(Direction.Credit, 15.99m, "AMAZON RETURN", "2026-08-14"),
            Lines.Card(Direction.Credit, 1500.00m, "PAYMENT", "2026-08-20"),
        ];
        var totals = new StatementTotals(August1, August31, 1500.00m, 94.94m, 4);

        var result = ReconcileGate.CheckExtraction(SourceKind.Card, rows, totals);

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void Should_Fail_When_CardTotalsOnlyReconcileWithTheBankSign()
    {
        List<StatementLine> rows = [Lines.Card(Direction.Debit, 100.00m, "CHARGE", "2026-08-03")];
        var totals = new StatementTotals(null, null, 500.00m, 400.00m, null);

        var result = ReconcileGate.CheckExtraction(SourceKind.Card, rows, totals);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Should_Fail_When_RowCountDiffersFromPrintedCount()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), BankTotals(count: 8));

        Assert.False(result.Ok);
        Assert.Contains("7 rows", result.Message, StringComparison.Ordinal);
        Assert.Contains("8", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_SkipCountCheck_When_StatementPrintsNoCount()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), BankTotals(count: null));

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void Should_Fail_When_ARowIsOutsideThePeriod()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), BankTotals(start: new DateOnly(2026, 8, 3)));

        Assert.False(result.Ok);
        Assert.Contains("2026-08-02", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CheckOnlyTheGivenBound_When_PeriodIsHalfKnown()
    {
        var totals = new StatementTotals(null, new DateOnly(2026, 8, 27), 13195.87m, 10230.45m, 7);

        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), totals);

        Assert.False(result.Ok);
        Assert.Contains("2026-08-28", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 10230.45)]
    [InlineData(13195.87, null)]
    [InlineData(null, null)]
    public void Should_FailAsNotVerifiable_When_OpeningOrClosingIsMissing(double? opening, double? closing)
    {
        var totals = BankTotals(opening: (decimal?)opening, closing: (decimal?)closing);

        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, BankRows(), totals);

        Assert.False(result.Ok);
        Assert.False(result.Verified);
        Assert.StartsWith(ReconcileResult.NotVerifiable, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_PassVerified_When_StatementHasNoTransactionsAndBalancesMatch()
    {
        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, [], new StatementTotals(August1, August31, 50m, 50m, 0));

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Verified);
    }

    [Fact]
    public void Should_Pass_When_OnlySomeRowsPrintABalance()
    {
        var rows = BankRows().Select((r, i) => i % 2 == 0 ? r : r with { Balance = null }).ToList();

        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, rows, BankTotals());

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void Should_Fail_When_PrintedBalancesContradictTheAmountsBetweenThem()
    {
        // Two amounts swapped between rows: totals still reconcile, the running balances do not.
        var rows = BankRows();
        (rows[3], rows[4]) = (rows[3] with { Amount = rows[4].Amount }, rows[4] with { Amount = rows[3].Amount });

        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, rows, BankTotals());

        Assert.False(result.Ok);
        Assert.Contains("running balance", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Pass_When_RowsAreNewestFirst()
    {
        var newestFirst = BankRows().AsEnumerable().Reverse().ToList();

        var result = ReconcileGate.CheckExtraction(SourceKind.Bank, newestFirst, BankTotals());

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void Should_ReportEveryFailure_When_SeveralChecksFail()
    {
        var result = ReconcileGate.CheckExtraction(
            SourceKind.Bank, BankRows(), BankTotals(closing: 1m, count: 9, end: new DateOnly(2026, 8, 20)));

        Assert.False(result.Ok);
        Assert.Contains("closing", result.Message, StringComparison.Ordinal);
        Assert.Contains("9", result.Message, StringComparison.Ordinal);
        Assert.Contains("period", result.Message, StringComparison.Ordinal);
    }
}
