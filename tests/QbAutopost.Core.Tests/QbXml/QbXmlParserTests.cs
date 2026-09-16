using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.QbXml;

public sealed class QbXmlParserTests
{
    private readonly IReadOnlyList<Core.Models.PostResult> _results =
        QbXmlParser.ParseAddResponse(Fixtures.Read("qbxml", "add-response.xml"));

    [Fact]
    public void Should_ReturnOneResultPerAddRs_When_ParsingResponse()
    {
        Assert.Equal(["2bfd9134c296636c", "a115e997de486513", "0b79b4babdcbf7a4"], _results.Select(r => r.RequestId));
    }

    [Fact]
    public void Should_ReadTxnIdAndAmount_When_CheckSucceeded()
    {
        var check = _results[0];

        Assert.Equal(0, check.StatusCode);
        Assert.Equal("1A2B-1787000001", check.TxnId);
        Assert.Equal("1787000001", check.EditSequence);
        Assert.Equal(311.40m, check.Amount);
    }

    [Fact]
    public void Should_KeepErrorMessageAndNoTxnId_When_LineWasRefused()
    {
        var refused = _results[1];

        Assert.Equal(3140, refused.StatusCode);
        Assert.Null(refused.TxnId);
        Assert.Contains("Office Supplies", refused.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReadDepositTotalAsAmount_When_DepositSucceeded()
    {
        Assert.Equal(2400.00m, _results[2].Amount);
    }

    [Fact]
    public void Should_Throw_When_ResponseHasNoMessageSet()
    {
        Assert.Throws<FormatException>(() => QbXmlParser.ParseAddResponse("<QBXML />"));
    }
}
