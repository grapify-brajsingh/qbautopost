using System.Runtime.Versioning;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.QuickBooks;

public sealed record QbGatewayOptions
{
    public required string AppName { get; init; }
    public required string CompanyFile { get; init; }

    /// <summary>Receives each SDK session step for the host's log; this project has no logging dependency.</summary>
    public Action<string>? Trace { get; init; }
}

/// <summary>
/// <see cref="IQbGateway"/> over the Desktop SDK (FR-11): one <see cref="QbSession"/> per call, on its own STA thread.
/// A COM call cannot be cancelled, so the token is only checked before the session opens; the busy timeout and the
/// one-call-at-a-time rule are applied by <c>ResilientQbGateway</c> around this class.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QbGateway(QbGatewayOptions options) : IQbGateway
{
    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
        RunOnStaThread(session => session.ProcessRequest(qbxml), ct);

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        RunOnStaThread(session => session.CurrentCompanyFile(), ct);

    /// <summary>M17 §4.7: one session on <paramref name="target"/>'s company file.</summary>
    public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) =>
        RunOnStaThread(session => session.ProcessRequest(qbxml), ct, target.CompanyFile);

    /// <inheritdoc cref="ProcessAsync(QbTarget, string, CancellationToken)"/>
    public Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) =>
        RunOnStaThread(session => session.CurrentCompanyFile(), ct, target.CompanyFile);

    private Task<string> RunOnStaThread(Func<QbSession, string> work, CancellationToken ct, string? companyFile = null)
    {
        var file = companyFile ?? options.CompanyFile;
        // A target that names the configured file is the default company: its session failures keep today's handling.
        var defaultCompany = string.Equals(file, options.CompanyFile, StringComparison.OrdinalIgnoreCase);
        ct.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var session = QbSession.Open(options.AppName, file, options.Trace, defaultCompany);
                result.SetResult(work(session));
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "QuickBooks SDK",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
