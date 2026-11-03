using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.QuickBooks;

namespace QbAutopost.Api.Logging;

/// <summary>
/// What the host logs once at startup (spec §14): the process (bitness and Windows session matter for the QuickBooks
/// SDK) and the effective settings with whether each file exists. Keys are reported as set or not set, never as values.
/// </summary>
public static class StartupLog
{
    private const string Found = "found";

    /// <param name="connection">The QuickBooks connection when it is simulated (M15 §4: its seed is stated), else null.</param>
    public static void Write(ILogger log, AppSettings s, IHostEnvironment environment, QbConnection? connection = null)
    {
        using var process = Process.GetCurrentProcess();
        log.LogInformation(
            "QbAutopost {Version} starting: {Environment} environment, {Runtime} on {Os}, {Bitness} process, PID {Pid}, Windows session {Session}, user {User}, program folder {ProgramFolder}",
            Version(), environment.EnvironmentName, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), Environment.ProcessId, process.SessionId,
            Environment.UserName, AppContext.BaseDirectory);
        if (OperatingSystem.IsWindows() && process.SessionId == 0)
        {
            log.LogWarning("Running in Windows session 0 (a service): the QuickBooks SDK needs the signed-in desktop session where QuickBooks runs");
        }

        log.LogInformation(
            "Settings: API {Bind} (legacy routes {LegacyRoutes}), DryRunDefault {DryRunDefault}, company {CompanyName}, company file {CompanyFile} ({CompanyFileState}), rules {RulesFile} ({RulesState})",
            s.Api.Bind, s.Api.LegacyRoutes ? "on" : "off", s.DryRunDefault, s.Company.Name, s.Company.FilePath, State(s.Company.FilePath), s.Company.RulesFile, State(s.Company.RulesFile));
        log.LogInformation(
            "Data files: ledger {Ledger} ({LedgerState}), QuickBooks lists {QbLists} ({QbListsState}), job index {JobIndex} ({JobIndexState}), logs {Logs}",
            s.Paths.Ledger, State(s.Paths.Ledger), s.Paths.QbLists, State(s.Paths.QbLists), s.Paths.JobIndex, State(s.Paths.JobIndex), s.Paths.Logs);
        log.LogInformation(
            "QuickBooks: app name {AppName}, qbXML {QbXmlVersion}, duplicate window {DuplicateWindowDays} days, busy timeout {BusyTimeoutSeconds} s, backup folder {BackupFolder}, backup max age {BackupMaxAgeHours} h, {ListSync}, {ListsSource}",
            s.QuickBooks.AppName, s.QuickBooks.QbXmlVersion, s.QuickBooks.DuplicateWindowDays, s.QuickBooks.BusyTimeoutSeconds,
            string.IsNullOrWhiteSpace(s.QuickBooks.BackupFolder) ? "(not set: backup age not checked)" : s.QuickBooks.BackupFolder,
            s.QuickBooks.BackupMaxAgeHours,
            ListSyncText(s.QuickBooks.ListSync),
            (s.Database.Configured ? "lists in the database" : "lists in " + s.Paths.QbLists) + SimulatedText(connection));
        if (s.OpenRouter.Enabled)
        {
            log.LogInformation(
                "OpenRouter: {OpenRouterUrl}, model {Model}, key {OpenRouterKeyState}, timeout {TimeoutSeconds} s; OCR {OcrState}",
                s.OpenRouter.BaseUrl, s.OpenRouter.Model, s.OpenRouter.ApiKey.Length > 0 ? "set" : "not set", s.OpenRouter.TimeoutSeconds,
                s.Ocr.Enabled ? "enabled, tessdata " + s.Ocr.TessDataPath : "disabled");
        }
        else
        {
            log.LogWarning(
                "OpenRouter: disabled (OpenRouter:Enabled=false, no AI): requirement read by the regex parser; PDF statements, invoices and lines no rule resolves are held; OCR {OcrState}",
                s.Ocr.Enabled ? "enabled, tessdata " + s.Ocr.TessDataPath : "disabled");
        }

        if (State(s.Company.FilePath) != Found)
        {
            log.LogWarning(
                "Company file {CompanyFile} is {State}: QuickBooks calls fail until Company:FilePath names the company file QuickBooks has open",
                s.Company.FilePath, State(s.Company.FilePath));
        }

        if (State(s.Company.RulesFile) != Found)
        {
            log.LogWarning("Rules file {RulesFile} is {State}: every job will fail until it exists", s.Company.RulesFile, State(s.Company.RulesFile));
        }
    }

    // M15 §4: counts only, never the names.
    private static string SimulatedText(QbConnection? connection) =>
        connection is not { Mode: QbConnectionMode.Simulated }
            ? string.Empty
            : connection.SimulatedSeed is { } seed
                ? FormattableString.Invariant($", simulated company: {seed.Accounts} accounts, {seed.Vendors} vendors, {seed.Customers} customers from {seed.File}")
                : ", simulated company: no lists";

    // M14 §11: the schedule as the worker will run it; 0 is "off" (the validator already rejected values below 5).
    private static string ListSyncText(ListSyncSettings l) =>
        l.IntervalMinutes == 0
            ? "list sync schedule off"
            : FormattableString.Invariant($"list sync every {l.IntervalMinutes} min (first after {l.FirstDelayMinutes} min)");

    private static string State(string path) =>
        string.IsNullOrWhiteSpace(path) ? "not set" : File.Exists(path) ? Found : "missing";

    private static string Version() =>
        typeof(StartupLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StartupLog).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
