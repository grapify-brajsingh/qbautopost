using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Stands in for the COM probe (CLAUDE.md rule 1: no test may touch a real QuickBooks SDK). The default answer is a
/// healthy x64 server; a test sets <see cref="Info"/> to describe a broken one.
/// </summary>
public sealed class FakeQbSdkProbe : IQbSdkProbe
{
    public static readonly QbSdkInfo Healthy = new(
        Ok: true,
        RequestProcessor: new QbSdkRequestProcessor(true, "QBXMLRP2.RequestProcessor", "{FAKE-CLSID}", true),
        ProcessBitness: "x64",
        QuickBooks: new QbSdkProcess(true, "QBW.EXE", "x64"),
        BitnessMatch: true,
        QbXmlVersion: "16.0",
        Message: "QuickBooks Desktop SDK ready");

    public int Calls { get; private set; }

    public QbSdkInfo Info { get; set; } = Healthy;

    public Task<QbSdkInfo> ProbeAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Info);
    }
}
