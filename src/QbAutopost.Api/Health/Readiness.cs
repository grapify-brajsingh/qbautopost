using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Health;

/// <summary>One readiness check (FR-A-2). <paramref name="Severity"/> decides whether a failure blocks readiness.</summary>
public sealed record ReadinessCheck(string Name, bool Ok, string Severity, string Message);

/// <summary><c>GET /api/v1/health/ready</c> body: <paramref name="Ok"/> is false iff an <c>error</c> check failed.</summary>
public sealed record ReadinessView(bool Ok, IReadOnlyList<ReadinessCheck> Checks);

/// <summary>
/// FR-A-2: can this host accept work? Local state only — no QuickBooks, no Hermes, no network — so a monitor or
/// <c>start-all.ps1</c> may poll it while a job posts.
/// </summary>
public static class Readiness
{
    /// <summary>A failure that makes the host not ready.</summary>
    public const string Error = "error";

    /// <summary>A failure worth reporting that still leaves the host ready.</summary>
    public const string Warning = "warning";

    public static ReadinessView Evaluate(AppSettings settings, bool workerStarted)
    {
        var checks = new List<ReadinessCheck>
        {
            Settings(settings),
            Rules(settings),
            QbLists(settings),
            LogFolder(settings),
            Worker(workerStarted),
        };
        return new ReadinessView(checks.All(c => c.Ok || c.Severity != Error), checks);
    }

    private static ReadinessCheck Settings(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.Company.Name)
            ? new ReadinessCheck("settings", false, Error, "Company:Name is not set")
            : new ReadinessCheck("settings", true, Error, $"company '{s.Company.Name}'");

    private static ReadinessCheck Rules(AppSettings s) =>
        // Spec §11: every job maps through rules.json, so a missing one fails every job rather than posting a guess.
        File.Exists(s.Company.RulesFile)
            ? new ReadinessCheck("rules", true, Error, s.Company.RulesFile)
            : new ReadinessCheck("rules", false, Error, $"{s.Company.RulesFile} not found; every job would fail");

    private static ReadinessCheck QbLists(AppSettings s) =>
        // Only a warning: FR-15 lists are how names are validated, but a job every rule resolves still posts.
        File.Exists(s.Paths.QbLists)
            ? new ReadinessCheck("qb-lists", true, Warning, s.Paths.QbLists)
            : new ReadinessCheck("qb-lists", false, Warning, $"{s.Paths.QbLists} not found; run POST /api/v1/quickbooks/lists/sync");

    private static ReadinessCheck LogFolder(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(s.Paths.Logs);
            return new ReadinessCheck("logFolder", true, Error, $"{s.Paths.Logs} writable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ReadinessCheck("logFolder", false, Error, $"{s.Paths.Logs}: {ex.Message}");
        }
    }

    private static ReadinessCheck Worker(bool started) =>
        started
            ? new ReadinessCheck("worker", true, Error, "started")
            : new ReadinessCheck("worker", false, Error, "the job worker has not started yet");
}
