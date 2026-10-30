using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Health;

/// <summary>One readiness check (FR-A-2). <paramref name="Severity"/> decides whether a failure blocks readiness.</summary>
public sealed record ReadinessCheck(string Name, bool Ok, string Severity, string Message, string? Reason = null);

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

    public static ReadinessView Evaluate(AppSettings settings, bool workerStarted, IQbListsSource lists, DatabaseGate? database = null)
    {
        var checks = new List<ReadinessCheck>
        {
            Settings(settings),
            Rules(settings),
            QbLists(settings, lists),
            LogFolder(settings),
            Worker(workerStarted),
        };
        if (database is not null)
        {
            checks.Add(Database(database));
        }

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

    private static ReadinessCheck QbLists(AppSettings s, IQbListsSource lists)
    {
        // Only a warning: FR-15 lists are how names are validated, but a job every rule resolves still posts.
        try
        {
            var current = lists.Current;
            var last = lists.LastAttempt;
            if (current.SyncedUtc is not { } synced)
            {
                var failed = last is { Outcome: not ListSyncOutcome.Ok } bad ? $" ({AttemptText(bad)})" : "";
                var status = lists.Status();
                var sync = !status.Synced && s.Database.Configured ? " or Sync now" : "";
                return new ReadinessCheck("qb-lists", status.Synced, Warning, status.Message + sync + failed);
            }

            // Spec §9: ok, with the age and any failed or skipped newest attempt named in the text.
            var message = last is { Outcome: not ListSyncOutcome.Ok } attempt
                ? $"{AttemptText(attempt)}; lists from {Hm(synced)}"
                : $"{(s.Database.Configured ? "database" : s.Paths.QbLists)}; lists from {Hm(synced)}";
            return new ReadinessCheck("qb-lists", true, Warning, message);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A corrupt, truncated or access-denied file must not turn a health poll into a 500.
            return new ReadinessCheck("qb-lists", false, Warning, $"{s.Paths.QbLists} is unreadable; run POST /api/v1/quickbooks/lists/sync");
        }
    }

    private static string AttemptText(ListSyncRun run) =>
        $"last sync {run.Outcome.ToString().ToLowerInvariant()} {Hm(run.FinishedUtc)}{(string.IsNullOrEmpty(run.Reason) ? "" : $" ({run.Reason})")}";

    private static string Hm(DateTime utc) => utc.ToUniversalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// Saved-requests spec 10.7: readiness is per feature. A database outage is reported but is only a warning, so a
    /// load balancer does not take the whole service out for a dashboard outage (Q-83). Never the connection string.
    /// </summary>
    private static ReadinessCheck Database(DatabaseGate gate) =>
        gate.IsReady
            ? new ReadinessCheck("database", true, Warning, "ready")
            : new ReadinessCheck("database", false, Warning, gate.Reason ?? "unreachable", gate.Reason);

    private static ReadinessCheck Worker(bool started) =>
        started
            ? new ReadinessCheck("worker", true, Error, "started")
            : new ReadinessCheck("worker", false, Error, "the job worker has not started yet");
}
