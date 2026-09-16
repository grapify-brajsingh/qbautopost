using QbAutopost.Core.Abstractions;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Core.Pipeline;

/// <summary>FR-16 / §6 <c>GET /health/quickbooks</c> body.</summary>
public sealed record QbHealthResult(bool Ok, string? CompanyFile, string Message);

/// <summary>
/// FR-16: QuickBooks is healthy iff <c>HostQuery</c> answers status 0. The message names the product (or the error, with
/// the process bitness, the usual cause of a failed session); the company file comes from the SDK session.
/// </summary>
public sealed class QbHealth(PipelineOptions options, IQbGateway gateway)
{
    public async Task<QbHealthResult> CheckAsync(CancellationToken ct)
    {
        HostInfo host;
        try
        {
            host = QbHostQuery.Parse(await gateway.ProcessAsync(QbHostQuery.Build(options.QbXmlVersion), ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new QbHealthResult(false, null, $"{ex.Message} (process is {Bitness})");
        }

        if (host.Status.Code != 0)
        {
            return new QbHealthResult(false, null, $"HostQuery failed: {host.Status}");
        }

        string? companyFile;
        try
        {
            companyFile = await gateway.CurrentCompanyFileAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // HostQuery succeeded, which is the FR-16 condition; the missing file name is only reported.
            return new QbHealthResult(true, null, $"{host.Product ?? "QuickBooks"}; company file unknown: {ex.Message}");
        }

        return new QbHealthResult(true, companyFile, host.Product ?? "QuickBooks answered HostQuery");
    }

    private static string Bitness => Environment.Is64BitProcess ? "x64" : "x86";
}
