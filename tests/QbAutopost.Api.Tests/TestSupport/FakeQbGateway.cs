using System.Globalization;
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
    private readonly List<string?> _targets = [];
    private readonly Dictionary<string, SimulatedQuickBooks> _companies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The configured company file: untargeted calls go to it and <see cref="Company"/> answers for it.</summary>
    public const string DefaultCompanyFile = @"C:\fake\Tropicana.QBW";

    public SimulatedQuickBooks Company { get; } = new("FAKE-");

    /// <summary>
    /// M17 §4.7: the file QuickBooks has open. When set, a call needing another file is refused with
    /// <see cref="QuickBooksWrongCompanyException"/> before anything is recorded (counted in <see cref="Refusals"/>).
    /// </summary>
    public string? OpenCompanyFile { get; set; }

    /// <summary>
    /// M17 ruling B1: a company file whose session cannot open. A call needing it is refused with
    /// <see cref="QuickBooksCompanyFileUnavailableException"/> before anything is recorded (counted in <see cref="UnavailableCalls"/>).
    /// </summary>
    public string? UnavailableCompanyFile { get; set; }

    /// <summary>Calls refused because another file is open.</summary>
    public int Refusals { get; private set; }

    /// <summary>Calls refused because <see cref="UnavailableCompanyFile"/> could not be opened.</summary>
    public int UnavailableCalls { get; private set; }

    /// <summary>The target file of each recorded request, in order; null for an untargeted one.</summary>
    public IReadOnlyList<string?> Targets
    {
        get
        {
            lock (_gate)
            {
                return _targets.ToList();
            }
        }
    }

    /// <summary>The simulated company behind <paramref name="file"/>; the default file is <see cref="Company"/>.</summary>
    public SimulatedQuickBooks CompanyFor(string file)
    {
        if (string.Equals(file, DefaultCompanyFile, StringComparison.OrdinalIgnoreCase))
        {
            return Company;
        }

        lock (_gate)
        {
            if (!_companies.TryGetValue(file, out var company))
            {
                company = new SimulatedQuickBooks("FAKE-");
                _companies[file] = company;
            }

            return company;
        }
    }

    /// <summary>1-based position of a request in a write message set that QuickBooks refuses.</summary>
    public int? RejectLine { get; set; }

    public Exception? Throw { get; set; }

    /// <summary>
    /// R31: 1-based position of an add QuickBooks creates (status 0, a TxnID) but answers with an amount one dollar
    /// off, so G5 holds it as <c>amount-mismatch</c> while the transaction exists in the company.
    /// </summary>
    public int? MisstateAmountLine { get; set; }

    public bool Hang { get; set; }

    /// <summary>
    /// T-1014: when set, a write message set is recorded and then waits for this task before it is answered, so a test
    /// can hold a post mid-send (two racing posts, a client that disconnects) and then let it finish.
    /// </summary>
    public Task? WriteGate { get; set; }

    public Exception? QueryThrow { get; set; }

    /// <summary>
    /// M14 T-1407: when set, a read-only message set is recorded and then waits for this task before it is answered
    /// (or fails with <see cref="QueryThrow"/>), so a test can hold a list sync mid-call.
    /// </summary>
    public Task? QueryGate { get; set; }

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

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => ProcessForAsync(null, qbxml, ct);

    public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) => ProcessForAsync(target, qbxml, ct);

    private void RefuseIfNotOpenable(string needed)
    {
        if (OpenCompanyFile is { } open && !string.Equals(open, needed, StringComparison.OrdinalIgnoreCase))
        {
            Refusals++;
            throw new QuickBooksWrongCompanyException(
                needed, open, $"simulated: QuickBooks has {open} open, so {needed} could not be opened");
        }

        if (UnavailableCompanyFile is { } unavailable && string.Equals(unavailable, needed, StringComparison.OrdinalIgnoreCase))
        {
            UnavailableCalls++;
            throw new QuickBooksCompanyFileUnavailableException(needed, "0x80040408: simulated: could not open the company file");
        }
    }

    private async Task<string> ProcessForAsync(QbTarget? target, string qbxml, CancellationToken ct)
    {
        var needed = target?.CompanyFile ?? DefaultCompanyFile;
        RefuseIfNotOpenable(needed);
        lock (_gate)
        {
            _requests.Add(qbxml);
            _targets.Add(target?.CompanyFile);
        }

        var readOnly = QbXmlRequests.IsReadOnly(qbxml);
        if (readOnly && QueryGate is { } queryGate)
        {
            await queryGate.WaitAsync(ct);
        }

        if ((readOnly ? QueryThrow : Throw) is { } error)
        {
            throw error;
        }

        if (!readOnly && FailWritesTimes > 0)
        {
            FailWritesTimes--;
            throw new QuickBooksUnavailableException("simulated: could not open a session");
        }

        if (!readOnly && WriteGate is { } gate)
        {
            await gate.WaitAsync(ct);
        }

        if (readOnly ? QueryHang : Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        var answer = CompanyFor(needed).Process(qbxml, readOnly ? null : Reject);
        return readOnly || MisstateAmountLine is not { } misstated ? answer : Misstate(answer, misstated);
    }

    private static string Misstate(string answer, int position)
    {
        var doc = XDocument.Parse(answer);
        var rs = doc.Root!.Element("QBXMLMsgsRs")!.Elements().ElementAtOrDefault(position - 1);
        var amount = rs?.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Ret", StringComparison.Ordinal)) is { } ret
            ? ret.Element("Amount") ?? ret.Element("DepositTotal")
            : null;
        if (amount is not null)
        {
            amount.Value = (decimal.Parse(amount.Value, CultureInfo.InvariantCulture) + 1m).ToString("0.00", CultureInfo.InvariantCulture);
        }

        return doc.Declaration + doc.ToString();
    }

    /// <summary>How often the company file was asked for — FR-A-3 requires the SDK probe never to ask.</summary>
    public int CompanyFileCalls { get; private set; }

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => CompanyFileForAsync(null);

    public Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) => CompanyFileForAsync(target);

    private Task<string> CompanyFileForAsync(QbTarget? target)
    {
        var needed = target?.CompanyFile ?? DefaultCompanyFile;
        try
        {
            RefuseIfNotOpenable(needed);
        }
        catch (QuickBooksUnavailableException ex)
        {
            return Task.FromException<string>(ex);
        }

        CompanyFileCalls++;
        return CompanyFileThrow is { } error ? Task.FromException<string>(error) : Task.FromResult(needed);
    }

    private XElement? Reject(int position, XElement request) =>
        RejectLine == position && request.Name.LocalName.EndsWith("AddRq", StringComparison.Ordinal)
            ? SimulatedQuickBooks.Response(
                request,
                RejectStatusCode,
                "There is an invalid reference to QuickBooks Account in the Check.")
            : null;
}
