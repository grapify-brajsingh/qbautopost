namespace QbAutopost.Core.Abstractions;

/// <summary>
/// QuickBooks Desktop SDK boundary (plan §1). The real implementation lives in <c>QbAutopost.QuickBooks</c> (COM, M6);
/// tests use <c>FakeQbGateway</c>. Calls are serialised by the single job worker.
/// </summary>
public interface IQbGateway
{
    /// <summary>Sends one qbXML message set and returns the raw qbXML response.</summary>
    Task<string> ProcessAsync(string qbxml, CancellationToken ct);

    Task<string> CurrentCompanyFileAsync(CancellationToken ct);
}
