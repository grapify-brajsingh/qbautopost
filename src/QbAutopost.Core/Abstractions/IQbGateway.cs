namespace QbAutopost.Core.Abstractions;

/// <summary>M17 §4.7: the company file a call is for, when it is not the configured default (<c>Company:FilePath</c>).</summary>
public sealed record QbTarget(string CompanyFile);

/// <summary>
/// QuickBooks Desktop SDK boundary (plan §1). The real implementation lives in <c>QbAutopost.QuickBooks</c> (COM, M6);
/// tests use <c>FakeQbGateway</c>. Calls are serialised by the single job worker.
/// </summary>
public interface IQbGateway
{
    /// <summary>Sends one qbXML message set to the configured company file and returns the raw qbXML response.</summary>
    Task<string> ProcessAsync(string qbxml, CancellationToken ct);

    Task<string> CurrentCompanyFileAsync(CancellationToken ct);

    /// <summary>
    /// M17 §4.7: sends one qbXML message set to <paramref name="target"/>'s company file. A gateway that serves only the
    /// configured file refuses it before anything is sent.
    /// </summary>
    Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) =>
        Task.FromException<string>(OnlyTheConfiguredFile(target));

    /// <summary>Opens a session on <paramref name="target"/>'s company file and asks which file QuickBooks has open.</summary>
    Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) =>
        Task.FromException<string>(OnlyTheConfiguredFile(target));

    private static QuickBooksUnavailableException OnlyTheConfiguredFile(QbTarget target) =>
        new($"this gateway serves only the configured company file, not {target.CompanyFile}");
}

/// <summary>
/// Thrown by a gateway when it could not reach QuickBooks <b>before</b> sending anything (no session, not configured).
/// Any other exception from <see cref="IQbGateway.ProcessAsync(string, CancellationToken)"/> means the request may have
/// been applied.
/// </summary>
public class QuickBooksUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// M17 §4.7: QuickBooks has another company file open, so <c>BeginSession</c> on <see cref="NeededFile"/> was refused.
/// Nothing was sent; the call is never retried (QuickBooks is never switched by this app).
/// </summary>
public sealed class QuickBooksWrongCompanyException(string neededFile, string? openFile, string message, Exception? inner = null)
    : QuickBooksUnavailableException(message, inner)
{
    public string NeededFile { get; } = neededFile;

    /// <summary>The file QuickBooks has open, when the SDK can report it.</summary>
    public string? OpenFile { get; } = openFile;
}

/// <summary>
/// M17 §4.7, ruling B1: <c>BeginSession</c> on a company file other than the configured default failed and the cause is
/// not certain (another file open, not approved, not reachable). Nothing was sent; the call is not retried.
/// </summary>
public sealed class QuickBooksCompanyFileUnavailableException(string neededFile, string sdkMessage, Exception? inner = null)
    : QuickBooksUnavailableException($"could not open the QuickBooks company file '{neededFile}' ({sdkMessage})", inner)
{
    public string NeededFile { get; } = neededFile;

    /// <summary>What the SDK said (HRESULT and message).</summary>
    public string SdkMessage { get; } = sdkMessage;
}

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
