namespace QbAutopost.Core.Abstractions;

/// <summary>
/// The COM class that fronts the Desktop SDK (FR-A-3): is it there, and can a connection be opened at all?
/// <paramref name="ProgId"/> is carried as data supplied by the COM layer — naming it here would put a COM detail in
/// Core (CLAUDE.md rule 2, enforced by <c>CoreIsolationTests</c>).
/// </summary>
public sealed record QbSdkRequestProcessor(bool Registered, string? ProgId, string? Clsid, bool CanOpenConnection);

/// <summary>The QuickBooks process itself, when one is running. Its bitness is what the host must match.</summary>
public sealed record QbSdkProcess(bool Running, string? Name, string? Bitness);

/// <summary>
/// <c>GET /api/v1/health/sdk</c> body (FR-A-3). Deliberately says nothing about a company file: the probe never calls
/// <c>BeginSession</c>, so this answers even with QuickBooks closed or a certificate dialog pending.
/// </summary>
public sealed record QbSdkInfo(
    bool Ok,
    QbSdkRequestProcessor RequestProcessor,
    string ProcessBitness,
    QbSdkProcess QuickBooks,
    bool BitnessMatch,
    string QbXmlVersion,
    string Message)
{
    /// <summary>The answer where the SDK cannot exist at all (not Windows, or the app is not configured for it).</summary>
    public static QbSdkInfo Unavailable(string processBitness, string qbXmlVersion, string message, string? progId = null) => new(
        Ok: false,
        RequestProcessor: new QbSdkRequestProcessor(false, progId, null, false),
        ProcessBitness: processBitness,
        QuickBooks: new QbSdkProcess(false, null, null),
        BitnessMatch: false,
        QbXmlVersion: qbXmlVersion,
        Message: message);
}

/// <summary>
/// FR-A-3. Implemented over COM in <c>QbAutopost.QuickBooks</c> only (CLAUDE.md rule 2); everywhere else answers
/// <see cref="QbSdkInfo.Unavailable"/>.
/// </summary>
public interface IQbSdkProbe
{
    Task<QbSdkInfo> ProbeAsync(CancellationToken ct);
}
