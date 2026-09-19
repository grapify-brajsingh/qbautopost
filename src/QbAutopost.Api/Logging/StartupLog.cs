using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Logging;

/// <summary>
/// What the host logs once at startup (spec §14): the process (bitness and Windows session matter for the QuickBooks
/// SDK) and the effective settings with whether each file exists. Keys are reported as set or not set, never as values.
/// </summary>
public static class StartupLog
{
    private const string Found = "found";

    public static void Write(ILogger log, AppSettings s, IHostEnvironment environment)
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
            "Settings: API {Bind}, DryRunDefault {DryRunDefault}, company {CompanyName}, company file {CompanyFile} ({CompanyFileState}), rules {RulesFile} ({RulesState})",
            s.Api.Bind, s.DryRunDefault, s.Company.Name, s.Company.FilePath, State(s.Company.FilePath), s.Company.RulesFile, State(s.Company.RulesFile));
        log.LogInformation(
            "Data files: ledger {Ledger} ({LedgerState}), QuickBooks lists {QbLists} ({QbListsState}), job index {JobIndex} ({JobIndexState}), logs {Logs}",
            s.Paths.Ledger, State(s.Paths.Ledger), s.Paths.QbLists, State(s.Paths.QbLists), s.Paths.JobIndex, State(s.Paths.JobIndex), s.Paths.Logs);
        log.LogInformation(
            "QuickBooks: app name {AppName}, qbXML {QbXmlVersion}, duplicate window {DuplicateWindowDays} days, busy timeout {BusyTimeoutSeconds} s, backup folder {BackupFolder}, backup max age {BackupMaxAgeHours} h",
            s.QuickBooks.AppName, s.QuickBooks.QbXmlVersion, s.QuickBooks.DuplicateWindowDays, s.QuickBooks.BusyTimeoutSeconds,
            string.IsNullOrWhiteSpace(s.QuickBooks.BackupFolder) ? "(not set: backup age not checked)" : s.QuickBooks.BackupFolder,
            s.QuickBooks.BackupMaxAgeHours);
        if (s.Hermes.Enabled)
        {
            log.LogInformation(
                "Hermes: {HermesUrl}, model {Model}, timeout {TimeoutSeconds} s, key {HermesKeyState}; OCR {OcrState}",
                s.Hermes.BaseUrl, s.Hermes.Model, s.Hermes.TimeoutSeconds, s.Hermes.ApiKey.Length > 0 ? "set" : "not set",
                s.Ocr.Enabled ? "enabled, tessdata " + s.Ocr.TessDataPath : "disabled");
        }
        else
        {
            log.LogWarning(
                "Hermes: disabled (Hermes:Enabled=false, no AI): requirement read by the regex parser; PDF statements, invoices and lines no rule resolves are held; OCR {OcrState}",
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

    private static string State(string path) =>
        string.IsNullOrWhiteSpace(path) ? "not set" : File.Exists(path) ? Found : "missing";

    private static string Version() =>
        typeof(StartupLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StartupLog).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
