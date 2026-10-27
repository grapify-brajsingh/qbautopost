using System.Runtime.InteropServices;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// FR-A-3 where the SDK cannot be there: not Windows, or the gateway is simulated/unconfigured. Answers honestly
/// rather than throwing — a health endpoint that 500s tells an operator nothing.
/// </summary>
public sealed class UnavailableQbSdkProbe(string qbXmlVersion, string reason) : IQbSdkProbe
{
    public Task<QbSdkInfo> ProbeAsync(CancellationToken ct) => Task.FromResult(
        QbSdkInfo.Unavailable(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), qbXmlVersion, reason));
}
