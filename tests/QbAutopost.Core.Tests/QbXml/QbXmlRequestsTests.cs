using QbAutopost.Core.QbXml;

namespace QbAutopost.Core.Tests.QbXml;

public sealed class QbXmlRequestsTests
{
    private static string MessageSet(string body) =>
        $"<?xml version=\"1.0\"?><?qbxml version=\"13.0\"?><QBXML><QBXMLMsgsRq onError=\"continueOnError\">{body}</QBXMLMsgsRq></QBXML>";

    [Theory]
    [InlineData("<CheckQueryRq requestID=\"1\" />", true)]
    [InlineData("<AccountQueryRq /><VendorQueryRq />", true)]
    [InlineData("<CheckQueryRq /><CheckAddRq />", false)]
    [InlineData("<TxnDelRq />", false)]
    [InlineData("", false)]
    public void Should_TellReadOnlyMessageSets_When_Classifying(string body, bool expected)
    {
        Assert.Equal(expected, QbXmlRequests.IsReadOnly(MessageSet(body)));
    }

    [Fact]
    public void Should_TreatAsWrite_When_RequestIsNotXml()
    {
        Assert.False(QbXmlRequests.IsReadOnly("not xml"));
    }
}
