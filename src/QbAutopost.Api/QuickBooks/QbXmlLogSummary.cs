using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// One request or response of a qbXML message set, as it is logged. Only names, ids and status: the body holds
/// statement text (memos), which spec §14 keeps out of the log.
/// </summary>
public sealed record QbXmlLogMessage(
    string Name, string? RequestId, int? StatusCode, string? Severity, string? StatusMessage, string? TxnId, int Returned);

/// <summary>Reads what a qbXML message set contains, for the QuickBooks call log.</summary>
public static class QbXmlLogSummary
{
    /// <summary>The children of <c>QBXMLMsgsRq</c>/<c>QBXMLMsgsRs</c>; null when the text is not readable qbXML.</summary>
    public static IReadOnlyList<QbXmlLogMessage>? Read(string qbxml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(qbxml);
        }
        catch (XmlException)
        {
            return null;
        }

        var set = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName is "QBXMLMsgsRq" or "QBXMLMsgsRs");
        return set?.Elements().Select(Message).ToList();
    }

    /// <summary>Request or response names in order, counted: <c>CheckAddRq x3, DepositAddRq x1</c>.</summary>
    public static string Describe(IReadOnlyList<QbXmlLogMessage> messages) =>
        messages.Count == 0
            ? "an empty message set"
            : string.Join(", ", messages.GroupBy(m => m.Name, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"));

    private static QbXmlLogMessage Message(XElement e)
    {
        // An add answers with <XxxRet><TxnID>, a delete with <TxnID> directly; a query may return many (no single id).
        var ids = e.Elements("TxnID").Concat(e.Elements().Elements("TxnID")).ToList();
        return new QbXmlLogMessage(
            e.Name.LocalName,
            (string?)e.Attribute("requestID"),
            int.TryParse((string?)e.Attribute("statusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null,
            (string?)e.Attribute("statusSeverity"),
            (string?)e.Attribute("statusMessage"),
            ids.Count == 1 ? ids[0].Value : null,
            e.Elements().Count(x => x.Name.LocalName.EndsWith("Ret", StringComparison.Ordinal)));
    }
}
