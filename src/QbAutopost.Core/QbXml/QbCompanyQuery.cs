using System.Xml.Linq;

namespace QbAutopost.Core.QbXml;

/// <summary>The answer to <c>CompanyQuery</c>: status plus the names QuickBooks holds for the open company file.</summary>
public sealed record QbCompanyInfo(QbStatus Status, string? CompanyName, string? LegalCompanyName);

/// <summary>M17 §3: <c>CompanyQueryRq</c> (golden file <c>tests/fixtures/qbxml/company-query.golden.xml</c>) and its reader.</summary>
public static class QbCompanyQuery
{
    public static string Build(string qbXmlVersion = QbXmlBuilder.DefaultVersion) =>
        QbMessageSet.Build(qbXmlVersion, "stopOnError", w =>
        {
            w.WriteStartElement("CompanyQueryRq");
            w.WriteAttributeString("requestID", "1");
            w.WriteEndElement();
        });

    public static QbCompanyInfo Parse(string qbxml)
    {
        var rs = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRs")?.Element("CompanyQueryRs")
            ?? throw new FormatException("qbXML response has no CompanyQueryRs element.");
        var ret = rs.Element("CompanyRet");
        return new QbCompanyInfo(QbStatus.Of(rs), ret?.Element("CompanyName")?.Value, ret?.Element("LegalCompanyName")?.Value);
    }
}
