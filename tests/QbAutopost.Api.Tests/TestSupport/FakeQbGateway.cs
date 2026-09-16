using System.Xml.Linq;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Records every request and answers with synthetic <c>*AddRs</c> (spec §15): sequential TxnIDs, the request amount
/// echoed back. Can be told to refuse line N, throw, or hang until cancelled.
/// </summary>
public sealed class FakeQbGateway : IQbGateway
{
    public const int RejectStatusCode = 3140;

    private readonly object _gate = new();
    private readonly List<string> _requests = [];
    private int _nextTxn;

    /// <summary>1-based position of a request in the message set that QuickBooks refuses.</summary>
    public int? RejectLine { get; set; }

    public Exception? Throw { get; set; }

    public bool Hang { get; set; }

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public async Task<string> ProcessAsync(string qbxml, CancellationToken ct)
    {
        lock (_gate)
        {
            _requests.Add(qbxml);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return Respond(qbxml);
    }

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult(@"C:\fake\Tropicana.QBW");

    private string Respond(string qbxml)
    {
        var requests = XDocument.Parse(qbxml).Root!.Element("QBXMLMsgsRq")!.Elements().ToList();
        var responses = new XElement("QBXMLMsgsRs");
        for (var i = 0; i < requests.Count; i++)
        {
            var rq = requests[i];
            var name = rq.Name.LocalName[..^"AddRq".Length]; // CheckAddRq → Check
            var rs = new XElement(name + "AddRs", new XAttribute("requestID", (string)rq.Attribute("requestID")!));
            if (RejectLine == i + 1)
            {
                rs.Add(
                    new XAttribute("statusCode", RejectStatusCode),
                    new XAttribute("statusSeverity", "Error"),
                    new XAttribute("statusMessage", "There is an invalid reference to QuickBooks Account in the Check."));
            }
            else
            {
                var txnId = $"FAKE-{Interlocked.Increment(ref _nextTxn)}";
                rs.Add(
                    new XAttribute("statusCode", 0),
                    new XAttribute("statusSeverity", "Info"),
                    new XAttribute("statusMessage", "Status OK"),
                    new XElement(name + "Ret", new XElement("TxnID", txnId), new XElement("EditSequence", "1"), EchoAmount(rq)));
            }

            responses.Add(rs);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("QBXML", responses)).ToString();
    }

    private static XElement EchoAmount(XElement rq) =>
        rq.Name.LocalName == "DepositAddRq"
            ? new XElement("DepositTotal", rq.Descendants("DepositLineAdd").Elements("Amount").Single().Value)
            : new XElement("Amount", rq.Descendants("ExpenseLineAdd").Elements("Amount").Single().Value);
}
