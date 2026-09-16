using System.Xml.Linq;

namespace QbAutopost.Core.QbXml;

/// <summary>Facts about an outgoing qbXML message set.</summary>
public static class QbXmlRequests
{
    /// <summary>
    /// True when every request in the message set only reads (<c>*QueryRq</c>), so sending it twice cannot change the
    /// company file. Unparseable input counts as not read-only.
    /// </summary>
    public static bool IsReadOnly(string qbxml)
    {
        try
        {
            var requests = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRq")?.Elements().ToList();
            return requests is { Count: > 0 }
                   && requests.All(r => r.Name.LocalName.EndsWith("QueryRq", StringComparison.Ordinal));
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
