using System.Diagnostics;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Core.Pipeline;

/// <summary>One measured step of the connection check (FR-A-4).</summary>
public sealed record ConnectionStep(string Name, bool Ok, long Ms, string? Message = null);

/// <summary><c>POST /api/v1/quickbooks/connection/test</c> body (FR-A-4).</summary>
public sealed record ConnectionCheckResult(
    bool Ok,
    string? CompanyFile,
    string? Product,
    string QbXmlVersion,
    IReadOnlyList<ConnectionStep> Steps,
    long TotalMs,
    string Message);

/// <summary>
/// FR-A-4: the same round trip as <see cref="QbHealth"/>, but timed step by step and allowed to take longer than the
/// busy timeout.
/// <para>
/// Two things the plain health check could not tell an operator, both from session 13: how much of the delay was spent
/// *waiting for another call* rather than for QuickBooks, and whether a slow answer still succeeded. A certificate
/// dialog held <c>BeginSession</c> for 101 s there; at the 60 s busy timeout the call was abandoned as
/// "quickbooks-busy" although the session itself was fine.
/// </para>
/// <para>
/// The steps are what this abstraction can honestly measure. <c>OpenConnection2</c>, <c>BeginSession</c> and
/// <c>EndSession</c> all happen inside one gateway call (one call = one SDK session, FR-11), so they are timed
/// together as <c>hostQuery</c> rather than reported as separate numbers that would be invented here. See Q-56.
/// </para>
/// </summary>
public sealed class QbConnectionCheck(PipelineOptions options, ResilientQbGateway gateway)
{
    public const string WaitStep = "waitForGateway";
    public const string HostQueryStep = "hostQuery";
    public const string CompanyFileStep = "companyFile";

    private const string SlowHint =
        "slow: a QuickBooks dialog (certificate or permission) usually causes this; it must be answered on the QuickBooks desktop";

    public async Task<ConnectionCheckResult> RunAsync(TimeSpan timeout, bool includeCompanyInfo, CancellationToken ct)
    {
        var steps = new List<ConnectionStep>();
        var total = Stopwatch.StartNew();
        var waited = TimeSpan.Zero;
        void OnWaited(TimeSpan wait) => waited = wait;

        gateway.Waited += OnWaited;
        try
        {
            var step = Stopwatch.StartNew();
            string response;
            try
            {
                response = await gateway.ProcessWithTimeoutAsync(QbHostQuery.Build(options.QbXmlVersion), timeout, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                step.Stop();
                steps.Add(new ConnectionStep(WaitStep, true, (long)waited.TotalMilliseconds, WaitMessage(waited)));
                steps.Add(new ConnectionStep(HostQueryStep, false, step.ElapsedMilliseconds, ex.Message));
                return Failed(steps, total, $"{ex.Message} (process is {Bitness})");
            }

            step.Stop();
            steps.Add(new ConnectionStep(WaitStep, true, (long)waited.TotalMilliseconds, WaitMessage(waited)));
            steps.Add(new ConnectionStep(HostQueryStep, true, step.ElapsedMilliseconds, Hint(step.Elapsed)));

            var host = QbHostQuery.Parse(response);
            if (host.Status.Code != 0)
            {
                // Reached QuickBooks, which refused: a different failure from "could not connect", and named as such.
                steps[^1] = steps[^1] with { Ok = false, Message = host.Status.ToString() };
                return Failed(steps, total, $"HostQuery failed: {host.Status}");
            }

            var product = host.Product ?? "QuickBooks answered HostQuery";
            if (!includeCompanyInfo)
            {
                return Done(steps, total, product, companyFile: null, "connected");
            }

            var fileStep = Stopwatch.StartNew();
            try
            {
                var companyFile = await gateway.CurrentCompanyFileAsync(timeout, ct);
                fileStep.Stop();
                steps.Add(new ConnectionStep(CompanyFileStep, true, fileStep.ElapsedMilliseconds, Hint(fileStep.Elapsed)));
                return Done(steps, total, product, companyFile, "connected");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // FR-16: HostQuery answering is what "ok" means; the company file is extra, so this is not a failure.
                fileStep.Stop();
                steps.Add(new ConnectionStep(CompanyFileStep, false, fileStep.ElapsedMilliseconds, ex.Message));
                return Done(steps, total, product, companyFile: null, $"connected; company file unknown: {ex.Message}");
            }
        }
        finally
        {
            gateway.Waited -= OnWaited;
        }
    }

    private static string? WaitMessage(TimeSpan waited) =>
        waited > TimeSpan.FromSeconds(1) ? "waited for another QuickBooks call to finish" : null;

    /// <summary>FR-A-4: past the ordinary busy timeout the call is still fine, but the operator is told why.</summary>
    private string? Hint(TimeSpan elapsed) => elapsed > gateway.BusyTimeout ? SlowHint : null;

    private ConnectionCheckResult Done(List<ConnectionStep> steps, Stopwatch total, string product, string? companyFile, string message)
    {
        total.Stop();
        return new ConnectionCheckResult(true, companyFile, product, options.QbXmlVersion, steps, total.ElapsedMilliseconds, message);
    }

    private ConnectionCheckResult Failed(List<ConnectionStep> steps, Stopwatch total, string message)
    {
        total.Stop();
        return new ConnectionCheckResult(false, null, null, options.QbXmlVersion, steps, total.ElapsedMilliseconds, message);
    }

    private static string Bitness => Environment.Is64BitProcess ? "x64" : "x86";
}
