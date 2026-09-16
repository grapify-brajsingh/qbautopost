using QbAutopost.Core.Extract;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Tests.Extract;

/// <summary>Layout detection and row rules shared by the CSV and XLSX parsers (spec FR-3).</summary>
public sealed class StatementGridParserTests
{
    private static readonly CsvLayout Signed = new()
    {
        Kind = SourceKind.Bank,
        HeaderContains = ["Date", "Memo", "Amount"],
        DateColumn = "Date",
        DateFormat = "yyyy-MM-dd",
        DescriptionColumn = "Memo",
        AmountColumn = "Amount",
    };

    private static readonly CsvLayout WithAccount = Signed with
    {
        HeaderContains = ["Date", "Memo", "Amount", "Account"],
        Last4Column = "Account",
    };

    [Fact]
    public void Should_HoldAsAmbiguousLayout_When_TwoLayoutsMatchTheHeader()
    {
        var parser = Parser(("first", Signed), ("second", Signed with { HeaderContains = ["Date", "Amount"] }));

        var result = parser.Parse("export-4521.csv", [["Date", "Memo", "Amount"], ["2026-08-01", "FEE", "-5.00"]]);

        Assert.Equal(HoldReasons.AmbiguousCsvLayout, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("first", StringComparison.Ordinal) && e.Contains("second", StringComparison.Ordinal));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Should_MatchHeaderIgnoringCaseAndSpaces_When_DetectingTheLayout()
    {
        var result = Parser(("signed", Signed)).Parse("export-4521.csv", [[" date ", "MEMO", "amount "], ["2026-08-01", "FEE", "-5.00"]]);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("signed", result.Layout);
    }

    [Fact]
    public void Should_HoldAsUnknownLayout_When_LayoutListsNoHeaderCells()
    {
        var result = Parser(("empty", Signed with { HeaderContains = [] })).Parse("export-4521.csv", [["Date", "Memo", "Amount"]]);

        Assert.Equal(HoldReasons.UnknownCsvLayout, result.HoldReason);
    }

    [Fact]
    public void Should_HoldNamingTheColumn_When_MatchedLayoutNeedsAColumnTheFileLacks()
    {
        var result = Parser(("checks", Signed with { CheckNoColumn = "Check #" })).Parse("export-4521.csv", [["Date", "Memo", "Amount"]]);

        Assert.Equal(HoldReasons.UnknownCsvLayout, result.HoldReason);
        Assert.Equal("checks", result.Layout);
        Assert.Contains(result.Errors, e => e.Contains("Check #", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_HoldAsUnknownLayout_When_GridIsEmpty()
    {
        var result = Parser(("signed", Signed)).Parse("export-4521.csv", []);

        Assert.Equal(HoldReasons.UnknownCsvLayout, result.HoldReason);
    }

    [Fact]
    public void Should_HoldAsConflictingLast4_When_AccountColumnHasTwoValues()
    {
        var result = Parser(("acct", WithAccount)).Parse(
            "export.csv",
            [["Date", "Memo", "Amount", "Account"], ["2026-08-01", "FEE", "-5.00", "xxxx4521"], ["2026-08-02", "FEE", "-6.00", "xxxx9012"]]);

        Assert.Equal(HoldReasons.ConflictingLast4, result.HoldReason);
    }

    [Fact]
    public void Should_HoldAsConflictingLast4_When_AccountColumnContradictsTheFileName()
    {
        var result = Parser(("acct", WithAccount)).Parse(
            "export-4521.csv",
            [["Date", "Memo", "Amount", "Account"], ["2026-08-01", "FEE", "-5.00", "9012"]]);

        Assert.Equal(HoldReasons.ConflictingLast4, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("4521", StringComparison.Ordinal) && e.Contains("9012", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_UseFileNameLast4_When_AccountColumnAgrees()
    {
        var result = Parser(("acct", WithAccount)).Parse(
            "export-4521.csv",
            [["Date", "Memo", "Amount", "Account"], ["2026-08-01", "FEE", "-5.00", "4521"], ["2026-08-02", "FEE", "-6.00", ""]]);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("4521", result.Last4);
    }

    [Fact]
    public void Should_TreatPositiveAsDebit_When_LayoutSaysSo()
    {
        var result = Parser(("card", Signed with { Kind = SourceKind.Card, PositiveIsDebit = true })).Parse(
            "card-7788.csv",
            [["Date", "Memo", "Amount"], ["2026-08-01", "CHARGE", "25.00"], ["2026-08-02", "PAYMENT", "-25.00"]]);

        Assert.Equal([Direction.Debit, Direction.Credit], result.Rows.Select(r => r.Direction));
        Assert.All(result.Rows, r => Assert.Equal(25.00m, r.Amount));
    }

    [Theory]
    [InlineData("5.00", "5.00")]
    [InlineData("", "")]
    [InlineData("abc", "")]
    public void Should_HoldWholeStatement_When_DebitCreditPairIsNotExactlyOneAmount(string debit, string credit)
    {
        var layout = Signed with { AmountColumn = null, DebitColumn = "Out", CreditColumn = "In", HeaderContains = ["Date", "Memo", "Out", "In"] };

        var result = Parser(("pair", layout)).Parse("export-4521.csv", [["Date", "Memo", "Out", "In"], ["2026-08-01", "X", debit, credit]]);

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
    }

    [Fact]
    public void Should_HoldWholeStatement_When_ARowHasNoDescription()
    {
        var result = Parser(("signed", Signed)).Parse("export-4521.csv", [["Date", "Memo", "Amount"], ["2026-08-01", " ", "-5.00"]]);

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("empty description", StringComparison.Ordinal));
    }

    private static StatementGridParser Parser(params (string Name, CsvLayout Layout)[] layouts) =>
        new(layouts.ToDictionary(l => l.Name, l => l.Layout));
}
