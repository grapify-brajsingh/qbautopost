using System.Xml.Linq;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.QbXml;

/// <summary>
/// FR-15: <c>AccountQuery</c>, <c>VendorQuery</c> and <c>CustomerQuery</c> (active only) in one message set, and the
/// reader for their response. Element names are pinned by <c>tests/fixtures/qbxml/list-query.golden.xml</c>.
/// </summary>
public static class QbListQuery
{
    public const string AccountsRequestId = "accounts";
    public const string VendorsRequestId = "vendors";
    public const string CustomersRequestId = "customers";

    /// <summary>qbXML "A query request did not find a matching object": an empty list, not an error.</summary>
    public const int NoMatchStatusCode = 1;

    public static string Build(string qbXmlVersion = QbXmlBuilder.DefaultVersion) =>
        QbMessageSet.Build(qbXmlVersion, "stopOnError", w =>
        {
            foreach (var (element, id) in new[]
                     {
                         ("AccountQueryRq", AccountsRequestId),
                         ("VendorQueryRq", VendorsRequestId),
                         ("CustomerQueryRq", CustomersRequestId),
                     })
            {
                w.WriteStartElement(element);
                w.WriteAttributeString("requestID", id);
                w.WriteElementString("ActiveStatus", "ActiveOnly");
                w.WriteEndElement();
            }
        });

    /// <summary>
    /// Reads the three lists. Accounts and customers use <c>FullName</c> (sub-accounts and jobs are addressed that way),
    /// vendors <c>Name</c>. Any status other than 0 or "no match" throws <see cref="QbStatusException"/>.
    /// </summary>
    public static QbLists Parse(string qbxml, DateTime syncedUtc)
    {
        var messages = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRs")
            ?? throw new FormatException("qbXML response has no QBXMLMsgsRs element.");

        var accounts = Rets(messages, "AccountQueryRs", "AccountRet")
            .Select(a => new QbAccount
            {
                Name = NameOf(a),
                Type = NullIfBlank(a.Element("AccountType")?.Value),
            })
            .ToList();
        var vendors = Rets(messages, "VendorQueryRs", "VendorRet").Select(NameOf).ToList();
        var customers = Rets(messages, "CustomerQueryRs", "CustomerRet").Select(NameOf).ToList();

        return new QbLists
        {
            SyncedUtc = syncedUtc,
            Accounts = accounts.Where(a => a.Name.Length > 0).ToList(),
            Vendors = vendors.Where(v => v.Length > 0).ToList(),
            Customers = customers.Where(c => c.Length > 0).ToList(),
        };
    }

    private static IEnumerable<XElement> Rets(XElement messages, string responseName, string retName)
    {
        var response = messages.Element(responseName)
            ?? throw new QbStatusException($"{responseName} is missing from the QuickBooks response");
        var status = QbStatus.Of(response);
        if (status.Code != 0 && status.Code != NoMatchStatusCode)
        {
            throw new QbStatusException($"{responseName} failed: {status}");
        }

        return response.Elements(retName);
    }

    private static string NameOf(XElement ret) =>
        (NullIfBlank(ret.Element("FullName")?.Value) ?? NullIfBlank(ret.Element("Name")?.Value)) ?? "";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
