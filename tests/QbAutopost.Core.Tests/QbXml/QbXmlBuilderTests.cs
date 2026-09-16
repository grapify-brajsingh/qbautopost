using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.QbXml;

/// <summary>Golden files pin the FR-9 element order; update them only with a tracker note (CLAUDE.md rule 5).</summary>
public sealed class QbXmlBuilderTests
{
    private static MappedTxn Post(StatementLine line, TxnKind kind, string account, string? payee, string lineAccount, string? refNumber) => new()
    {
        Line = line,
        Kind = kind,
        Decision = Decision.Post,
        Confidence = Confidence.Rule,
        Account = account,
        Payee = payee,
        LineAccount = lineAccount,
        RefNumber = refNumber,
    };

    private static void AssertGolden(string goldenFile, string actual) =>
        Assert.Equal(
            Fixtures.Read("qbxml", goldenFile).ReplaceLineEndings("\n").TrimEnd(),
            actual.ReplaceLineEndings("\n").TrimEnd());

    [Fact]
    public void Should_MatchGolden_When_BuildingChecks()
    {
        var txns = new[]
        {
            Post(Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY", date: "2026-08-12"),
                TxnKind.Check, "Chase Checking 4521", "Florida Power & Light", "Utilities", "ACH"),
            Post(Lines.Bank(Direction.Debit, 420.00m, "CHECK 1043", checkNo: "1043", date: "2026-08-05"),
                TxnKind.Check, "Chase Checking 4521", null, "Ask My Accountant", "1043"),
        };

        AssertGolden("check.golden.xml", QbXmlBuilder.BuildAddRequest(txns));
    }

    [Fact]
    public void Should_MatchGolden_When_BuildingCardChargeAndCredit()
    {
        var txns = new[]
        {
            Post(Lines.Card(Direction.Debit, 62.18m, "AMAZON.COM*RT4Y1 AMZN.COM/BILL", date: "2026-08-03"),
                TxnKind.CcCharge, "Chase Sapphire 7788", "Amazon", "Office Supplies", "ACH"),
            Post(Lines.Card(Direction.Credit, 15.99m, "AMAZON.COM*RF8K2 AMZN.COM/BILL", date: "2026-08-14"),
                TxnKind.CcCredit, "Chase Sapphire 7788", "Amazon", "Office Supplies", "ACH"),
        };

        AssertGolden("creditcard.golden.xml", QbXmlBuilder.BuildAddRequest(txns));
    }

    [Fact]
    public void Should_MatchGolden_When_BuildingDeposit()
    {
        var txns = new[]
        {
            Post(Lines.Bank(Direction.Credit, 2400.00m, "DEPOSIT PALM COURT RENTALS", date: "2026-08-08"),
                TxnKind.Deposit, "Chase Checking 4521", "Palm Court Rentals LLC", "Rental Income", null),
        };

        AssertGolden("deposit.golden.xml", QbXmlBuilder.BuildAddRequest(txns));
    }

    [Fact]
    public void Should_Throw_When_ALineIsHeld()
    {
        var held = Post(Lines.Bank(Direction.Debit, 1m, "X"), TxnKind.Check, "A", null, "B", "ACH") with { Decision = Decision.Hold };

        Assert.Throws<ArgumentException>(() => QbXmlBuilder.BuildAddRequest([held]));
    }

    [Fact]
    public void Should_Throw_When_PostableLineHasNoLineAccount()
    {
        var broken = Post(Lines.Bank(Direction.Debit, 1m, "X"), TxnKind.Check, "A", null, "B", "ACH") with { LineAccount = null };

        Assert.Throws<InvalidOperationException>(() => QbXmlBuilder.BuildAddRequest([broken]));
    }

    [Fact]
    public void Should_DropControlCharacters_When_MemoContainsThem()
    {
        var txn = Post(Lines.Bank(Direction.Debit, 1m, "BADMEMO <x>"), TxnKind.Check, "A", null, "B", "ACH");

        var xml = QbXmlBuilder.BuildAddRequest([txn]);

        Assert.Contains("<Memo>BADMEMO &lt;x&gt;</Memo>", xml, StringComparison.Ordinal);
    }
}
