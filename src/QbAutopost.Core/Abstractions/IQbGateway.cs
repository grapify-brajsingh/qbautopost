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

/// <summary>
/// FR-11: the call did not finish within <c>BusyTimeoutSeconds</c> and was abandoned. The request may or may not have been
/// applied, so the caller treats it like a failed call (G4 on the next attempt decides).
/// </summary>
public sealed class QuickBooksBusyException(string message) : Exception(message);

/// <summary>
/// The SDK failed while the request was being processed (a COM error from <c>ProcessRequest</c>). The request may have
/// been applied. <see cref="ErrorCode"/> is the SDK's HRESULT.
/// </summary>
public sealed class QuickBooksCallException(string message, int errorCode, Exception? inner = null) : Exception(message, inner)
{
    public int ErrorCode { get; } = errorCode;
}
