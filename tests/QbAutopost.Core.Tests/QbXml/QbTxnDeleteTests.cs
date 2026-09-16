using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.QbXml;

/// <summary>FR-13 TxnDel request golden file and response reading.</summary>
public sealed class QbTxnDeleteTests
{
    private static readonly TxnToDelete[] Txns =
    [
        new(TxnKind.Check, "1A2B-1787000001"),
        new(TxnKind.CcCharge, "1A2C-1787000002"),
        new(TxnKind.CcCredit, "1A2D-1787000003"),
        new(TxnKind.Deposit, "1A2E-1787000004"),
    ];

    private readonly IReadOnlyList<TxnDeleteResult> _results = QbTxnDelete.Parse(Txns, Fixtures.Read("qbxml", "txn-del-response.xml"));

    [Fact]
    public void Should_MatchGolden_When_BuildingDeletes()
    {
        Assert.Equal(
            Fixtures.Read("qbxml", "txn-del.golden.xml").ReplaceLineEndings("\n").TrimEnd(),
            QbTxnDelete.Build(Txns).ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Should_Throw_When_NothingToDelete()
    {
        Assert.Throws<ArgumentException>(() => QbTxnDelete.Build([]));
    }

    [Fact]
    public void Should_ReturnOneResultPerRequestInOrder_When_Parsing()
    {
        Assert.Equal(Txns, _results.Select(r => r.Txn));
    }

    [Fact]
    public void Should_MarkDeleted_When_StatusIsZeroAndTxnIdMatches()
    {
        Assert.Equal([true, false, true, false], _results.Select(r => r.Deleted));
    }

    [Fact]
    public void Should_KeepSdkMessage_When_DeleteIsRefused()
    {
        Assert.Equal(3120, _results[1].Status.Code);
        Assert.Contains("cannot be found", _results[1].Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NotMarkDeleted_When_AnswerIsMissing()
    {
        Assert.Equal(QbXmlParser.MissingStatusCode, _results[3].Status.Code);
    }

    [Fact]
    public void Should_NotMarkDeleted_When_SuccessNamesAnotherTxnId()
    {
        const string xml = "<QBXML><QBXMLMsgsRs><TxnDelRs requestID=\"1\" statusCode=\"0\"><TxnDelType>Check</TxnDelType><TxnID>OTHER</TxnID></TxnDelRs></QBXMLMsgsRs></QBXML>";

        var result = Assert.Single(QbTxnDelete.Parse([Txns[0]], xml));

        Assert.False(result.Deleted);
        Assert.Contains("OTHER", result.Status.Message, StringComparison.Ordinal);
    }
}
