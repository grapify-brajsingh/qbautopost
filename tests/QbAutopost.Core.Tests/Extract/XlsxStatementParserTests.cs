using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

public sealed class XlsxStatementParserTests : IDisposable
{
    private static readonly object?[] CardHeader = ["Transaction Date", "Post Date", "Description", "Category", "Type", "Amount", "Memo"];

    private readonly XlsxStatementParser _parser = new(Fixtures.SampleRules().CsvLayouts);
    private readonly TempJobFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Should_ReadSameRowsAsCsv_When_WorkbookHoldsSampleBankStatement()
    {
        var csv = new CsvStatementParser(Fixtures.SampleRules().CsvLayouts).Parse(Fixtures.SampleBankCsv);

        var result = _parser.Parse(Fixtures.PathOf("statements", "chase-checking-4521.xlsx"));

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("chase-checking", result.Layout);
        Assert.Equal(SourceKind.Bank, result.Kind);
        Assert.Equal("4521", result.Last4);
        Assert.Equal(
            csv.Rows.Select(r => (r.LineNo, r.Date, r.Description, r.Direction, r.Amount, r.Balance, r.CheckNo)),
            result.Rows.Select(r => (r.LineNo, r.Date, r.Description, r.Direction, r.Amount, r.Balance, r.CheckNo)));
    }

    [Fact]
    public void Should_ReadTextDates_When_TheyMatchLayoutFormat()
    {
        var path = Write("card-7788.xlsx", CardHeader, ["08/09/2026", "08/10/2026", "SHELL OIL 57442", "Gas", "Sale", -48.75m, null]);

        var row = Assert.Single(_parser.Parse(path).Rows);

        Assert.Equal(new DateOnly(2026, 8, 9), row.Date);
        Assert.Equal(Direction.Debit, row.Direction);
        Assert.Equal(48.75m, row.Amount);
    }

    [Fact]
    public void Should_HoldWholeStatement_When_TextDateDoesNotMatchLayoutFormat()
    {
        var path = Write("card-7788.xlsx", CardHeader, ["9 Aug 2026", "", "SHELL OIL 57442", "Gas", "Sale", -48.75m, null]);

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("bad date '9 Aug 2026'", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_HoldWholeStatement_When_DateCellIsAPlainNumber()
    {
        var path = Write("card-7788.xlsx", CardHeader, [46243, null, "SHELL OIL 57442", "Gas", "Sale", -48.75m, null]);

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
    }

    [Fact]
    public void Should_ReadOnlyFirstWorksheet_When_WorkbookHasSeveral()
    {
        var path = _temp.PathOf("statements", "card-7788.xlsx");
        XlsxBuilder.WriteSheets(
            path,
            ("Activity", [CardHeader, [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "Shopping", "Sale", -62.18m, null]]),
            ("Summary", [CardHeader, [new DateTime(2026, 8, 4), null, "SHOULD NOT BE READ", "", "Sale", -1.00m, null]]));

        var row = Assert.Single(_parser.Parse(path).Rows);

        Assert.Equal("AMAZON.COM*RT4Y1", row.Description);
    }

    [Fact]
    public void Should_SkipBlankRows_When_SheetHasGaps()
    {
        var path = Write(
            "card-7788.xlsx",
            [null, null],
            CardHeader,
            [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "Shopping", "Sale", -62.18m, null],
            [null, "  ", null],
            [new DateTime(2026, 8, 20), null, "AUTOMATIC PAYMENT - THANK YOU", "", "Payment", 1500m, null]);

        var result = _parser.Parse(path);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal([1, 2], result.Rows.Select(r => r.LineNo));
        Assert.Equal(Direction.Credit, result.Rows[1].Direction);
    }

    [Fact]
    public void Should_ReadDateTimeCellAsItsDate_When_CellHasATimeOfDay()
    {
        var path = Write("card-7788.xlsx", CardHeader, [new DateTime(2026, 8, 3, 14, 22, 0), null, "AMAZON.COM*RT4Y1", "", "Sale", -62.18m, null]);

        var row = Assert.Single(_parser.Parse(path).Rows);

        Assert.Equal(new DateOnly(2026, 8, 3), row.Date);
    }

    [Fact]
    public void Should_ReadFormulaResult_When_AmountCellHasAFormula()
    {
        var path = Write("card-7788.xlsx", CardHeader, [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "", "Sale", new Formula("-60-2.18"), null]);

        var row = Assert.Single(_parser.Parse(path).Rows);

        Assert.Equal(62.18m, row.Amount);
        Assert.Equal(Direction.Debit, row.Direction);
    }

    [Fact]
    public void Should_HoldWholeStatement_When_FormulaHasNoSavedResult()
    {
        var path = Write("card-7788.xlsx", CardHeader, [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "", "Sale", new Formula("-60-2.18", Calculated: false), null]);

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("bad or zero amount ''", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_KeepAmountExact_When_NumberHasBinaryFloatingPointNoise()
    {
        var path = Write("card-7788.xlsx", CardHeader, [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "", "Sale", 0.1 + 0.2, null]);

        var row = Assert.Single(_parser.Parse(path).Rows);

        Assert.Equal(0.30m, row.Amount);
        Assert.Equal(Direction.Credit, row.Direction);
    }

    [Fact]
    public void Should_HoldWholeStatement_When_AmountHasMoreThanTwoDecimals()
    {
        var path = Write("card-7788.xlsx", CardHeader, [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "", "Sale", -62.185m, null]);

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnparsableRows, result.HoldReason);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Should_HoldWholeStatement_When_AmountCellIsABoolean()
    {
        var path = Write("card-7788.xlsx", CardHeader, [new DateTime(2026, 8, 3), null, "AMAZON.COM*RT4Y1", "", "Sale", true, null]);

        Assert.Equal(HoldReasons.UnparsableRows, _parser.Parse(path).HoldReason);
    }

    [Fact]
    public void Should_UseDebitCreditAndNumericAccountColumns_When_LayoutHasThem()
    {
        var path = Write(
            "generic-export.xlsx",
            ["Date", "Details", "Debit", "Credit", "Account"],
            [new DateTime(2026, 8, 1), "WIRE FROM ACME", null, 1250m, 9012],
            ["2026-08-02", "SERVICE FEE", 15m, null, 9012]);

        var result = _parser.Parse(path);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("generic-debit-credit", result.Layout);
        Assert.Equal("9012", result.Last4);
        Assert.Equal([Direction.Credit, Direction.Debit], result.Rows.Select(r => r.Direction));
        Assert.Equal([1250.00m, 15.00m], result.Rows.Select(r => r.Amount));
    }

    [Fact]
    public void Should_ReadNumericCheckNumber_When_CellIsANumber()
    {
        var row = _parser.Parse(Fixtures.PathOf("statements", "chase-checking-4521.xlsx")).Rows.Single(r => r.CheckNo is not null);

        Assert.Equal("1043", row.CheckNo);
    }

    [Fact]
    public void Should_HoldStatement_When_NoLayoutMatchesHeader()
    {
        var path = Write("export-1111.xlsx", ["When", "What", "How much"], [new DateTime(2026, 8, 3), "X", 1m]);

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnknownCsvLayout, result.HoldReason);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Should_HoldStatement_When_FirstWorksheetIsEmpty()
    {
        var path = _temp.PathOf("statements", "card-7788.xlsx");
        XlsxBuilder.WriteSheets(path, ("Empty", []), ("Data", [CardHeader]));

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnknownCsvLayout, result.HoldReason);
    }

    [Fact]
    public void Should_HoldAsUnreadable_When_FileIsNotAWorkbook()
    {
        var path = _temp.WithFile(Path.Combine("statements", "card-7788.xlsx"), "not a zip").PathOf("statements", "card-7788.xlsx");

        var result = _parser.Parse(path);

        Assert.Equal(HoldReasons.UnreadableStatement, result.HoldReason);
        Assert.Equal("card-7788.xlsx", result.File);
        Assert.Single(result.Errors);
    }

    private string Write(string fileName, params object?[][] rows)
    {
        var path = _temp.PathOf("statements", fileName);
        XlsxBuilder.Write(path, rows);
        return path;
    }
}
