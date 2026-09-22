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

    /// <summary>
    /// T-901 (api-v1 §9): also map the flat paths of spec §6 (<c>/jobs</c>, <c>/health/*</c>, …) beside
    /// <c>/api/v1/*</c>. Default true for one release so the POC package and the deploy scripts keep working; each
    /// use is logged. Set false once no caller uses them (Q-53), after which they are deleted.
    /// </summary>
    public bool LegacyRoutes { get; set; } = true;

    /// <summary>FR-A-4: the largest <c>timeoutSeconds</c> a caller may ask for; above it the request is a 400.</summary>
    public int MaxConnectionTestTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// T-907 / §6.1: rows per direct request. A cap keeps one call from holding the QuickBooks lock for an hour;
    /// exceeding it refuses the batch rather than truncating it, because a truncated batch posts part of the money.
    /// </summary>
    public int MaxTransactionsPerRequest { get; set; } = 500;

    /// <summary>
    /// T-908 / FR-A-10: how long a direct post may hold the caller's connection before it answers 202 and lets them
    /// poll. The work is never cancelled by this — only the waiting is.
    /// </summary>
    public int SyncPostTimeoutSeconds { get; set; } = 120;
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

    /// <summary>
    /// T-904 / FR-A-4: budget for <c>POST /quickbooks/connection/test</c>, deliberately longer than
    /// <see cref="BusyTimeoutSeconds"/>. On the owner's server the certificate dialog held the first call 101 s, past
    /// the 60 s busy timeout, so the connection looked broken while the session was in fact fine.
    /// </summary>
    public int ConnectionTestTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// T-904 / Q-50: may a request name its own company file? Default false — one installation serves one company
    /// (Q-44), and an override would post into another company file without anyone choosing that.
    /// </summary>
    public bool AllowCompanyFileOverride { get; set; }

    /// <summary>Folders an overridden company file must sit inside, when the override is allowed at all.</summary>
    public IList<string> AllowedCompanyFolders { get; set; } = [];

    /// <summary>
    /// T-907 / §6.1: the largest amount one direct line may carry. A bigger line is <b>held</b>, not refused, so one
    /// unusual amount cannot fail a batch of hundreds — and a person still looks at it before that money moves.
    /// </summary>
    public decimal MaxLineAmount { get; set; } = 100000.00m;

    /// <summary>§6.1: how old or how far ahead a direct line's date may be. Outside it the row is held.</summary>
    public DateWindowSettings AllowedDateWindow { get; set; } = new();
}

/// <summary>§6.1 <c>QuickBooks:AllowedDateWindow</c>, in whole days either side of today.</summary>
public sealed class DateWindowSettings
{
    /// <summary>Default two years: older than that is far likelier to be a typo than a real posting.</summary>
    public int MaxAgeDays { get; set; } = 730;

    /// <summary>Tomorrow is allowed, because a caller's clock may be a time zone ahead of the server's.</summary>
    public int MaxFutureDays { get; set; } = 1;
}

public sealed class HermesSettings
{
    /// <summary>
    /// SPEC-GAP T-806: false = no AI at all (POC). The requirement is read by the regex parser; anything that needs a
    /// model (PDF statements, invoices, lines no rule resolves) is held as if Hermes were down.
    /// </summary>
    public bool Enabled { get; set; } = true;

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

    /// <summary>
    /// T-908 / §6.3: one folder per direct batch, holding its request, state and qbXML. Blank means "beside the
    /// ledger", which is where the rest of the app's data already lives.
    /// </summary>
    public string ApiBatches { get; set; } = "";

    /// <summary>Relative paths resolve against the content root.</summary>
    public void ResolveAgainst(string root)
    {
        Ledger = Path.GetFullPath(Ledger, root);
        QbLists = Path.GetFullPath(QbLists, root);
        Logs = Path.GetFullPath(Logs, root);
        JobIndex = Path.GetFullPath(JobIndex, root);
        ApiBatches = string.IsNullOrWhiteSpace(ApiBatches)
            ? Path.Combine(Path.GetDirectoryName(Ledger)!, "api-batches")
            : Path.GetFullPath(ApiBatches, root);
    }
}
