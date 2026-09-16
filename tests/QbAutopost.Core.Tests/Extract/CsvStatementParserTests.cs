using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

public sealed class CsvStatementParserTests
{
    private readonly CsvStatementParser _parser = new(Fixtures.SampleRules().CsvLayouts);

    [Fact]
    public void Should_ReadSevenRows_When_ParsingSampleBankStatement()
    {
        var result = _parser.Parse(Fixtures.SampleBankCsv);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(7, result.Rows.Count);
        Assert.Equal("chase-checking", result.Layout);
        Assert.Equal(SourceKind.Bank, result.Kind);
        Assert.Equal("4521", result.Last4);
    }

    [Fact]
    public void Should_ReadFourRows_When_ParsingSampleCardStatement()
    {
        var result = _parser.Parse(Fixtures.SampleCardCsv);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(SourceKind.Card, result.Kind);
        Assert.Equal("7788", result.Last4);
    }

    [Fact]
    public void Should_MapSignedAmountToPositiveAmountAndDirection_When_AmountIsNegative()
    {
        var homeDepot = _parser.Parse(Fixtures.SampleBankCsv).Rows.Single(r => r.Description.StartsWith("HOME DEPOT", StringComparison.Ordinal));

        Assert.Equal(Direction.Debit, homeDepot.Direction);
        Assert.Equal(184.32m, homeDepot.Amount);
        Assert.Equal(new DateOnly(2026, 8, 22), homeDepot.Date);
        Assert.Equal(9980.45m, homeDepot.Balance);
    }

    [Fact]
    public void Should_ReadCheckNumber_When_RowIsACheck()
    {
        var rows = _parser.Parse(Fixtures.SampleBankCsv).Rows;

        var check = Assert.Single(rows, r => r.CheckNo is not null);
        Assert.Equal("1043", check.CheckNo);
        Assert.Equal(420.00m, check.Amount);
    }

    [Fact]
    public void Should_TreatPositiveCardAmountAsCredit_When_LayoutUsesNegativeForCharges()
    {
        var rows = _parser.Parse(Fixtures.SampleCardCsv).Rows;

        Assert.Equal(2, rows.Count(r => r.Direction == Direction.Credit));
        Assert.Equal(2, rows.Count(r => r.Direction == Direction.Debit));
    }

    [Fact]
    public void Should_HoldStatement_When_NoLayoutMatchesHeader()
    {
        var result = _parser.Parse(Fixtures.PathOf("statements", "unknown-layout-1111.csv"));

        Assert.Equal(HoldReasons.UnknownCsvLayout, result.HoldReason);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Should_HoldWholeStatement_When_AnyRowHasABadAmount()
    {
        var result = _parser.Parse(Fixtures.PathOf("statements", "bad-amount-card-7788.csv"));

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
        Assert.Contains(result.Errors, e => e.StartsWith("row 2:", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_HoldAsUnknownAccount_When_NeitherFileNameNorContentHasLastFour()
    {
        var result = _parser.Parse(Fixtures.PathOf("statements", "card-without-account-number.csv"));

        Assert.Equal(HoldReasons.UnknownAccount, result.HoldReason);
    }

    [Fact]
    public void Should_UseDebitCreditColumnsAndAccountColumn_When_LayoutHasThem()
    {
        var result = _parser.Parse(Fixtures.PathOf("statements", "generic-export.csv"));

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("9012", result.Last4);
        Assert.Collection(
            result.Rows,
            wire =>
            {
                Assert.Equal("WIRE FROM \"ACME\", INC", wire.Description);
                Assert.Equal(Direction.Credit, wire.Direction);
                Assert.Equal(1250.00m, wire.Amount);
            },
            fee =>
            {
                Assert.Equal(Direction.Debit, fee.Direction);
                Assert.Equal(15.00m, fee.Amount);
            });
    }
}
