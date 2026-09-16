using System.Globalization;
using System.Xml.Linq;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.QbXml;

/// <summary>Reads QuickBooks qbXML responses (spec FR-12).</summary>
public static class QbXmlParser
{
    /// <summary>Unparseable or missing status codes map to this value, which never counts as success.</summary>
    public const int MissingStatusCode = -1;

    /// <summary>One <see cref="PostResult"/> per <c>*AddRs</c> element, in response order.</summary>
    public static IReadOnlyList<PostResult> ParseAddResponse(string qbxml)
    {
        var doc = XDocument.Parse(qbxml);
        var messages = doc.Root?.Element("QBXMLMsgsRs")
            ?? throw new FormatException("qbXML response has no QBXMLMsgsRs element.");

        return messages.Elements()
            .Where(e => e.Name.LocalName.EndsWith("AddRs", StringComparison.Ordinal))
            .Select(ParseAddRs)
            .ToList();
    }

    private static PostResult ParseAddRs(XElement rs)
    {
        var ret = rs.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Ret", StringComparison.Ordinal));
        var amountText = ret?.Element("Amount")?.Value ?? ret?.Element("DepositTotal")?.Value;

        return new PostResult
        {
            RequestId = (string?)rs.Attribute("requestID") ?? "",
            StatusCode = int.TryParse((string?)rs.Attribute("statusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                ? code
                : MissingStatusCode,
            StatusMessage = (string?)rs.Attribute("statusMessage"),
            TxnId = NullIfBlank(ret?.Element("TxnID")?.Value),
            EditSequence = NullIfBlank(ret?.Element("EditSequence")?.Value),
            Amount = decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
