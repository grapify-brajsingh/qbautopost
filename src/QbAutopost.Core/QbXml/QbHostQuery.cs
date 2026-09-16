using System.Xml.Linq;

namespace QbAutopost.Core.QbXml;

/// <summary>The answer to <c>HostQuery</c>: status plus the product description when it succeeded.</summary>
public sealed record HostInfo(QbStatus Status, string? Product);

/// <summary>FR-16: <c>HostQueryRq</c> (golden file <c>tests/fixtures/qbxml/host-query.golden.xml</c>) and its reader.</summary>
public static class QbHostQuery
{
    public static string Build(string qbXmlVersion = QbXmlBuilder.DefaultVersion) =>
        QbMessageSet.Build(qbXmlVersion, "stopOnError", w =>
        {
            w.WriteStartElement("HostQueryRq");
            w.WriteAttributeString("requestID", "1");
            w.WriteEndElement();
        });

    public static HostInfo Parse(string qbxml)
    {
        var rs = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRs")?.Element("HostQueryRs")
            ?? throw new FormatException("qbXML response has no HostQueryRs element.");
        var ret = rs.Element("HostRet");
        var product = ret?.Element("ProductName")?.Value;
        var major = ret?.Element("MajorVersion")?.Value;
        var minor = ret?.Element("MinorVersion")?.Value;
        var version = major is null ? null : minor is null ? major : $"{major}.{minor}";
        return new HostInfo(
            QbStatus.Of(rs),
            product is null ? null : version is null ? product : $"{product} {version}");
    }
}
