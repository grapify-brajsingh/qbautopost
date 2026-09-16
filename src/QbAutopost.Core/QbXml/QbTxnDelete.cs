using System.Globalization;
using System.Xml.Linq;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.QbXml;

/// <summary>One transaction to delete: its kind (→ <c>TxnDelType</c>) and QuickBooks TxnID.</summary>
public sealed record TxnToDelete(TxnKind Kind, string TxnId);

/// <summary>The answer for one <c>TxnDelRq</c>, matched back by position.</summary>
public sealed record TxnDeleteResult(TxnToDelete Txn, QbStatus Status)
{
    public bool Deleted => Status.Code == 0;
}

/// <summary>
/// FR-13: one message set of <c>TxnDelRq(TxnDelType, TxnID)</c> with <c>continueOnError</c>; requestIDs are the
/// 1-based positions. Element order is pinned by <c>tests/fixtures/qbxml/txn-del.golden.xml</c>.
/// </summary>
public static class QbTxnDelete
{
    public static string Build(IReadOnlyList<TxnToDelete> txns, string qbXmlVersion = QbXmlBuilder.DefaultVersion)
    {
        if (txns.Count == 0)
        {
            throw new ArgumentException("Nothing to delete.", nameof(txns));
        }

        return QbMessageSet.Build(qbXmlVersion, "continueOnError", w =>
        {
            for (var i = 0; i < txns.Count; i++)
            {
                w.WriteStartElement("TxnDelRq");
                w.WriteAttributeString("requestID", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("TxnDelType", QbTxnQuery.QbName(txns[i].Kind));
                QbMessageSet.WriteText(w, "TxnID", txns[i].TxnId);
                w.WriteEndElement();
            }
        });
    }

    /// <summary>
    /// One result per requested transaction, in request order. A missing or duplicated answer counts as not deleted
    /// (status <see cref="QbXmlParser.MissingStatusCode"/>).
    /// </summary>
    public static IReadOnlyList<TxnDeleteResult> Parse(IReadOnlyList<TxnToDelete> txns, string qbxml)
    {
        var messages = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRs")
            ?? throw new FormatException("qbXML response has no QBXMLMsgsRs element.");
        var byId = messages.Elements("TxnDelRs")
            .GroupBy(rs => (string?)rs.Attribute("requestID") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return txns.Select((txn, i) =>
        {
            var id = (i + 1).ToString(CultureInfo.InvariantCulture);
            if (!byId.TryGetValue(id, out var answers) || answers.Count != 1)
            {
                return new TxnDeleteResult(txn, new QbStatus(QbXmlParser.MissingStatusCode, "no single answer for this request"));
            }

            var rs = answers[0];
            var status = QbStatus.Of(rs);
            var echoed = rs.Element("TxnID")?.Value;
            if (status.Code == 0 && !string.Equals(echoed, txn.TxnId, StringComparison.Ordinal))
            {
                // SPEC-GAP T-606: success must name the transaction we asked for, else it is not marked undone.
                status = new QbStatus(QbXmlParser.MissingStatusCode, $"QuickBooks confirmed TxnID '{echoed}' instead");
            }

            return new TxnDeleteResult(txn, status);
        }).ToList();
    }
}
