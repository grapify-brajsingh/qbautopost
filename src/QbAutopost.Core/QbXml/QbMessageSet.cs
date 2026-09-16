using System.Globalization;
using System.Text;
using System.Xml;

namespace QbAutopost.Core.QbXml;

/// <summary>
/// Writes one qbXML request message set (<c>QBXML/QBXMLMsgsRq</c>) with the same layout as <see cref="QbXmlBuilder"/>:
/// UTF-8 without BOM, two-space indent, LF line ends. Used by the query and delete builders.
/// </summary>
internal static class QbMessageSet
{
    public static string Build(string qbXmlVersion, string onError, Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(false),
        };

        using var buffer = new Utf8StringWriter();
        using (var w = XmlWriter.Create(buffer, settings))
        {
            w.WriteStartDocument();
            w.WriteProcessingInstruction("qbxml", $"version=\"{qbXmlVersion}\"");
            w.WriteStartElement("QBXML");
            w.WriteStartElement("QBXMLMsgsRq");
            w.WriteAttributeString("onError", onError);
            body(w);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return buffer.ToString() + "\n";
    }

    /// <summary>Escaped text without characters XML cannot carry.</summary>
    public static void WriteText(XmlWriter w, string element, string value) =>
        w.WriteElementString(element, new string(value.Where(XmlConvert.IsXmlChar).ToArray()));

    public static void WriteDate(XmlWriter w, string element, DateOnly date) =>
        w.WriteElementString(element, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding { get; } = new UTF8Encoding(false);
    }
}
