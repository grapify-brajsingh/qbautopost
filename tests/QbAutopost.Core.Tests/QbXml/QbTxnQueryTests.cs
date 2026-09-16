using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.QbXml;

/// <summary>FR-8 G4 query: request golden files and response reading.</summary>
public sealed class QbTxnQueryTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertGolden(string goldenFile, string actual) =>
        Assert.Equal(
            Fixtures.Read("qbxml", goldenFile).ReplaceLineEndings("\n").TrimEnd(),
            actual.ReplaceLineEndings("\n").TrimEnd());

    [Fact]
    public void Should_MatchGolden_When_BuildingCheckQuery()
    {
        AssertGolden("txn-query-check.golden.xml", QbTxnQuery.Build(TxnKind.Check, "Chase Checking 4521", D("2026-07-30"), D("2026-08-25")));
    }

    [Fact]
    public void Should_MatchGoldenWithLineItems_When_BuildingDepositQuery()
    {
        AssertGolden("txn-query-deposit.golden.xml", QbTxnQuery.Build(TxnKind.Deposit, "Chase Checking 4521", D("2026-08-05"), D("2026-08-31")));
    }

    [Theory]
    [InlineData(TxnKind.Check, "CheckQueryRq")]
    [InlineData(TxnKind.CcCharge, "CreditCardChargeQueryRq")]
    [InlineData(TxnKind.CcCredit, "CreditCardCreditQueryRq")]
    [InlineData(TxnKind.Deposit, "DepositQueryRq")]
    public void Should_UseKindQueryElement_When_Building(TxnKind kind, string element)
    {
        Assert.Contains($"<{element} requestID=\"1\">", QbTxnQuery.Build(kind, "A", D("2026-08-01"), D("2026-08-02")), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReadCheckFields_When_ParsingResponse()
    {
        var found = QbTxnQuery.Parse(TxnKind.Check, Fixtures.Read("qbxml", "check-query-response.xml"));

        Assert.Equal(2, found.Count);
        Assert.Equal(
            new ExistingTxn
            {
                TxnId = "1A2B-1786000001",
                Date = D("2026-08-12"),
                Amount = 311.40m,
                Account = "Chase Checking 4521",
                Payee = "Florida Power & Light",
                RefNumber = "ACH",
            },
            found[0]);
        Assert.Null(found[1].Payee);
        Assert.Equal("1043", found[1].RefNumber);
    }

    [Fact]
    public void Should_ReadDepositTotalAndLineCustomer_When_ParsingDepositResponse()
    {
        var deposit = Assert.Single(QbTxnQuery.Parse(TxnKind.Deposit, Fixtures.Read("qbxml", "deposit-query-response.xml")));

        Assert.Equal(2400.00m, deposit.Amount);
        Assert.Equal("Palm Court Rentals LLC", deposit.Payee);
        Assert.Equal("Chase Checking 4521", deposit.Account);
        Assert.Null(deposit.RefNumber);
    }

    [Fact]
    public void Should_ReturnEmpty_When_QueryFindsNothing()
    {
        const string xml = "<QBXML><QBXMLMsgsRs><CheckQueryRs requestID=\"1\" statusCode=\"1\" statusMessage=\"none\" /></QBXMLMsgsRs></QBXML>";

        Assert.Empty(QbTxnQuery.Parse(TxnKind.Check, xml));
    }

    [Fact]
    public void Should_Throw_When_QueryIsRefused()
    {
        const string xml = "<QBXML><QBXMLMsgsRs><CheckQueryRs requestID=\"1\" statusCode=\"3100\" statusMessage=\"bad\" /></QBXMLMsgsRs></QBXML>";

        var ex = Assert.Throws<QbStatusException>(() => QbTxnQuery.Parse(TxnKind.Check, xml));

        Assert.Contains("3100: bad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Throw_When_ResponseIsForAnotherKind()
    {
        Assert.Throws<QbStatusException>(() => QbTxnQuery.Parse(TxnKind.Deposit, Fixtures.Read("qbxml", "check-query-response.xml")));
    }
}
