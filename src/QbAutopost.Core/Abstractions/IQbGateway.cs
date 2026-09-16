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

/// <summary>
/// Thrown by a gateway when it could not reach QuickBooks <b>before</b> sending anything (no session, not configured).
/// Any other exception from <see cref="IQbGateway.ProcessAsync"/> means the request may have been applied.
/// </summary>
public sealed class QuickBooksUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
