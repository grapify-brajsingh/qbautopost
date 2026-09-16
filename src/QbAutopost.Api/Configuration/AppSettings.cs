namespace QbAutopost.Api.Configuration;

/// <summary>
/// <c>appsettings.json</c> (spec §12). Secrets (<see cref="ApiSettings.ApiKey"/>, <see cref="HermesSettings.ApiKey"/>)
/// may come from <c>QBAUTOPOST__*</c> environment variables and are never logged or written to output.
/// </summary>
public sealed class AppSettings
{
    public ApiSettings Api { get; set; } = new();
    public bool DryRunDefault { get; set; } = true;
    public CompanySettings Company { get; set; } = new();
    public QuickBooksSettings QuickBooks { get; set; } = new();
    public HermesSettings Hermes { get; set; } = new();
    public OcrSettings Ocr { get; set; } = new();
    public PathsSettings Paths { get; set; } = new();

    /// <summary>Makes every configured file path absolute, relative ones against <paramref name="root"/>.</summary>
    public void ResolvePaths(string root)
    {
        Company.RulesFile = Path.GetFullPath(Company.RulesFile, root);
        Paths.ResolveAgainst(root);
        if (!string.IsNullOrWhiteSpace(QuickBooks.BackupFolder))
        {
            QuickBooks.BackupFolder = Path.GetFullPath(QuickBooks.BackupFolder, root);
        }

        if (!string.IsNullOrWhiteSpace(Ocr.TessDataPath))
        {
            Ocr.TessDataPath = Path.GetFullPath(Ocr.TessDataPath, root);
        }
    }
}

public sealed class ApiSettings
{
    public const string KeyHeader = "X-Api-Key";

    public string Bind { get; set; } = "http://127.0.0.1:5080";
    public string ApiKey { get; set; } = "";
}

public sealed class CompanySettings
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string RulesFile { get; set; } = "rules.json";
}

public sealed class QuickBooksSettings
{
    public string AppName { get; set; } = "QbAutopost";
    public string QbXmlVersion { get; set; } = "13.0";
    public int DuplicateWindowDays { get; set; } = 3;
    public int BusyTimeoutSeconds { get; set; } = 60;
    public string BackupFolder { get; set; } = "";
    public int BackupMaxAgeHours { get; set; } = 36;

    /// <summary>
    /// SPEC-GAP T-601: <c>true</c> answers from an in-memory simulated company instead of the SDK (plan T-601
    /// "QuickBooks:Fake"). Allowed only in Development/Testing because its TxnIDs would enter the real ledger.
    /// </summary>
    public bool Fake { get; set; }

    /// <summary>FR-11 pause before the one retry (5 s). Configurable only so tests do not wait.</summary>
    public double RetryDelaySeconds { get; set; } = 5;
}

public sealed class HermesSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8642";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "default";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class OcrSettings
{
    public bool Enabled { get; set; }
    public string TessDataPath { get; set; } = "";
}

public sealed class PathsSettings
{
    public string Ledger { get; set; } = "ledger.json";
    public string QbLists { get; set; } = "qb-lists.json";
    public string Logs { get; set; } = "logs";

    /// <summary>
    /// SPEC-GAP T-102: job id → folder list, so a restarted host can find each job's <c>status.json</c>
    /// (spec §6 "status.json scan of known folders" does not say where known folders are recorded).
    /// </summary>
    public string JobIndex { get; set; } = "jobs.json";

    /// <summary>Relative paths resolve against the content root.</summary>
    public void ResolveAgainst(string root)
    {
        Ledger = Path.GetFullPath(Ledger, root);
        QbLists = Path.GetFullPath(QbLists, root);
        Logs = Path.GetFullPath(Logs, root);
        JobIndex = Path.GetFullPath(JobIndex, root);
    }
}
