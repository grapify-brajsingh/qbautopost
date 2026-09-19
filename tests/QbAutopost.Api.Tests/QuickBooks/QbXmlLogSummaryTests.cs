using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.QuickBooks;

public sealed class QbXmlLogSummaryTests
{
    [Fact]
    public void Should_CountRequestsByName_When_MessageSetHasSeveral()
    {
        var messages = QbXmlLogSummary.Read(File.ReadAllText(Fixtures.PathOf("qbxml", "check.golden.xml")));

        Assert.NotNull(messages);
        Assert.Matches(@"^CheckAddRq x\d+$", QbXmlLogSummary.Describe(messages));
    }

    [Fact]
    public void Should_ReadStatusOfEveryResponse_When_ResponseParses()
    {
        var messages = QbXmlLogSummary.Read(File.ReadAllText(Fixtures.PathOf("qbxml", "add-response.xml")));

        Assert.NotNull(messages);
        Assert.Equal(
            [("CheckAddRs", 0, "1A2B-1787000001"), ("CreditCardChargeAddRs", 3140, null), ("DepositAddRs", 0, "1A2D-1787000003")],
            messages.Select(m => (m.Name, m.StatusCode ?? -1, m.TxnId)));
        Assert.Equal("Error", messages[1].Severity);
        Assert.Contains("invalid reference", messages[1].StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReturnNull_When_XmlIsUnreadable()
    {
        Assert.Null(QbXmlLogSummary.Read("<QBXML><QBXMLMsgsRs>"));
    }
}
