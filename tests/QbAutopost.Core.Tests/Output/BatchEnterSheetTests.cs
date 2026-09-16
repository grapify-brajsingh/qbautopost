using QbAutopost.Core.Models;
using QbAutopost.Core.Output;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Output;

public sealed class BatchEnterSheetTests
{
    private static MappedTxn Check(string description, Decision decision = Decision.Post) => new()
    {
        Line = Lines.Bank(Direction.Debit, 311.40m, description),
        Kind = TxnKind.Check,
        Decision = decision,
        Confidence = Confidence.Rule,
        Account = "Chase Checking 4521",
        Payee = "Florida Power & Light",
        LineAccount = "Utilities",
        RefNumber = "ACH",
    };

    [Fact]
    public void Should_WriteHeaderAndPostableRow_When_BuildingChecks()
    {
        var csv = BatchEnterSheet.BuildChecks([Check("ACH DEBIT FPL"), Check("HELD", Decision.Hold)]);

        Assert.Equal(
            "Bank,Date,Number,Payee,Account,Amount,Memo\r\n" +
            "Chase Checking 4521,2026-08-12,ACH,Florida Power & Light,Utilities,311.40,ACH DEBIT FPL\r\n",
            csv);
    }

    [Fact]
    public void Should_QuoteCell_When_TextContainsCommaOrQuote()
    {
        var csv = BatchEnterSheet.BuildChecks([Check("WIRE \"ACME\", INC")]);

        Assert.Contains(",\"WIRE \"\"ACME\"\", INC\"\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_PrefixApostrophe_When_TextStartsWithFormulaCharacter()
    {
        var csv = BatchEnterSheet.BuildChecks([Check("=HYPERLINK(\"x\")")]);

        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\"\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_WriteHeaderOnly_When_NoDepositsArePostable()
    {
        var csv = BatchEnterSheet.BuildDeposits([Check("ACH DEBIT FPL")]);

        Assert.Equal("Date,Received From,From Account,Memo,Amount,Deposit To\r\n", csv);
    }
}
