using System.Xml.Linq;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Records every request and answers from a <see cref="SimulatedQuickBooks"/> company (spec §15): sequential
/// <c>FAKE-n</c> TxnIDs, the request amount echoed back. <see cref="RejectLine"/>, <see cref="Throw"/> and
/// <see cref="Hang"/> apply to message sets that change data (adds, deletes); <see cref="QueryThrow"/> and
/// <see cref="QueryHang"/> to read-only ones (G4 queries, list sync, host query).
/// </summary>
public sealed class FakeQbGateway : IQbGateway
{
    public const int RejectStatusCode = 3140;

    private readonly object _gate = new();
    private readonly List<string> _requests = [];

    public SimulatedQuickBooks Company { get; } = new("FAKE-");

    /// <summary>1-based position of a request in a write message set that QuickBooks refuses.</summary>
    public int? RejectLine { get; set; }

    public Exception? Throw { get; set; }

    public bool Hang { get; set; }

    public Exception? QueryThrow { get; set; }

    public bool QueryHang { get; set; }

    /// <summary>Throws this many times (then answers) for write message sets.</summary>
    public int FailWritesTimes { get; set; }

    public Exception? CompanyFileThrow { get; set; }

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

    /// <summary>Message sets that add or delete transactions.</summary>
    public IReadOnlyList<string> Writes => Requests.Where(r => !QbXmlRequests.IsReadOnly(r)).ToList();

    /// <summary>Read-only message sets (queries).</summary>
    public IReadOnlyList<string> Queries => Requests.Where(QbXmlRequests.IsReadOnly).ToList();

    public async Task<string> ProcessAsync(string qbxml, CancellationToken ct)
    {
        lock (_gate)
        {
            _requests.Add(qbxml);
        }

        var readOnly = QbXmlRequests.IsReadOnly(qbxml);
        if ((readOnly ? QueryThrow : Throw) is { } error)
        {
            throw error;
        }

        if (!readOnly && FailWritesTimes > 0)
        {
            FailWritesTimes--;
            throw new QuickBooksUnavailableException("simulated: could not open a session");
        }

        if (readOnly ? QueryHang : Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return Company.Process(qbxml, readOnly ? null : Reject);
    }

    /// <summary>How often the company file was asked for — FR-A-3 requires the SDK probe never to ask.</summary>
    public int CompanyFileCalls { get; private set; }

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct)
    {
        CompanyFileCalls++;
        return CompanyFileThrow is { } error ? Task.FromException<string>(error) : Task.FromResult(@"C:\fake\Tropicana.QBW");
    }

    private XElement? Reject(int position, XElement request) =>
        RejectLine == position && request.Name.LocalName.EndsWith("AddRq", StringComparison.Ordinal)
            ? SimulatedQuickBooks.Response(
                request,
                RejectStatusCode,
                "There is an invalid reference to QuickBooks Account in the Check.")
            : null;
}
