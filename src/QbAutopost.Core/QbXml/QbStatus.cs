using System.Globalization;
using System.Xml.Linq;

namespace QbAutopost.Core.QbXml;

/// <summary>The <c>statusCode</c>/<c>statusMessage</c> pair of one qbXML response element.</summary>
public readonly record struct QbStatus(int Code, string? Message)
{
    public static QbStatus Of(XElement response) => new(
        int.TryParse((string?)response.Attribute("statusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code
            : QbXmlParser.MissingStatusCode,
        (string?)response.Attribute("statusMessage"));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Code}: {Message}");
}

/// <summary>QuickBooks answered, but refused a read (query) request.</summary>
public sealed class QbStatusException(string message) : Exception(message);
