using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Health;

/// <summary>Facts about the running process (FR-A-1). Bitness and the Windows session decide whether the SDK can work.</summary>
public sealed record ProcessView(string Bitness, DateTime StartedUtc, long UptimeSeconds, int WindowsSession);

/// <summary>The single job worker (FR-A-1): started, what it is on, and how much is waiting.</summary>
public sealed record WorkerView(bool Running, string? ActiveJobId, int QueueDepth);

/// <summary><c>GET /api/v1/health</c> body (FR-A-1).</summary>
public sealed record AppHealthView(
    bool Ok,
    string Status,
    string Version,
    string Environment,
    ProcessView Process,
    // The camelCase policy would render this "quickBooksGateway"; FR-A-1 names it "quickbooksGateway".
    [property: JsonPropertyName("quickbooksGateway")] string QuickBooksGateway,
    string Hermes,
    WorkerView Worker);

/// <summary>
/// FR-A-1: liveness from in-process state only — no COM, no network, no company file — so a monitor may poll it every
/// few seconds and still get an answer while a job holds the QuickBooks gateway.
/// </summary>
public sealed class AppHealth(
    IOptions<AppSettings> settings,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    QbConnection connection,
    JobQueue queue,
    IJobStore store)
{
    /// <summary>Process start, read once: it never changes and reading it costs a syscall.</summary>
    private static readonly DateTime ProcessStartedUtc = StartTimeUtc();

    public AppHealthView Now() => new(
        Ok: true,
        Status: "healthy",
        Version: BuildVersion,
        Environment: environment.EnvironmentName,
        Process: new ProcessView(
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ProcessStartedUtc,
            (long)(DateTime.UtcNow - ProcessStartedUtc).TotalSeconds,
            WindowsSession),
        QuickBooksGateway: connection.Mode.ToString().ToLowerInvariant(),
        Hermes: settings.Value.Hermes.Enabled ? "enabled" : "disabled",
        Worker: new WorkerView(
            lifetime.ApplicationStarted.IsCancellationRequested,
            store.All().FirstOrDefault(j => JobStatusRules.IsActive(j.Status))?.JobId,
            queue.Depth));

    /// <summary>Readiness (FR-A-2) shares the lifetime signal: the worker starts with the host.</summary>
    public ReadinessView Ready() =>
        Readiness.Evaluate(settings.Value, lifetime.ApplicationStarted.IsCancellationRequested);

    internal static string BuildVersion =>
        typeof(AppHealth).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppHealth).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static DateTime StartTimeUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // A health endpoint must still answer where the process table cannot be read.
            return DateTime.UtcNow;
        }
    }

    private static int WindowsSession
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.SessionId;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
            {
                return -1;
            }
        }
    }
}
