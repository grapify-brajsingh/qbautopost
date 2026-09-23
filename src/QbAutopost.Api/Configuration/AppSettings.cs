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

    /// <summary>
    /// T-910 / FR-A-13: the single Api:ApiKey keeps working as an implicit all-scopes client while this is true.
    /// Default true for one release so the POC package and the deploy scripts keep working; then default false.
    /// </summary>
    public bool AllowLegacyKey { get; set; } = true;

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

    /// <summary>
    /// T-909 / FR-A-12: how long an <c>Idempotency-Key</c> is remembered. Long enough that a caller's retry logic
    /// outlives any plausible outage; short enough that the file stays small.
    /// </summary>
    public int IdempotencyRetentionDays { get; set; } = 30;

    /// <summary>
    /// T-912 / FR-A-16: how long <c>audit-yyyyMMdd.jsonl</c> is kept. Far longer than the 31-day operational log,
    /// because this is the record of money moving and is asked for long after anybody cares why a job was slow.
    /// Zero or less keeps every file for ever, so a misconfigured number can never delete the evidence.
    /// </summary>
    public int AuditRetentionDays { get; set; } = 400;

    /// <summary>
    /// T-911 / FR-A-14: bind somewhere strangers can reach without TLS. Default false, and startup <b>refuses</b>
    /// rather than serving keys and amounts in the clear; turning it on warns at startup and on every request, so a
    /// temporary arrangement cannot quietly become the permanent one.
    /// </summary>
    public bool AllowInsecureRemote { get; set; }

    /// <summary>
    /// T-911 / FR-A-17: the largest request body accepted, in bytes (2 MB). T-909 buffers the whole body to hash it
    /// for the idempotency key, so this cap bounds that buffer as much as it bounds the parser.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 2 * 1024 * 1024;

    public TlsSettings Tls { get; set; } = new();

    public CorsSettings Cors { get; set; } = new();

    public RateLimitSettings RateLimits { get; set; } = new();
}

/// <summary>
/// T-911 / FR-A-14. <see cref="PfxPassword"/> is <b>environment only</b>: a value found in <c>appsettings.json</c> is
/// accepted — refusing would strand an operator mid-deploy — but warned about by name, never by value.
/// </summary>
public sealed class TlsSettings
{
    public string PfxPath { get; set; } = "";

    public string PfxPassword { get; set; } = "";

    public string StoreThumbprint { get; set; } = "";

    /// <summary>True when a certificate has been named at all, by file or by store.</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(PfxPath) || !string.IsNullOrWhiteSpace(StoreThumbprint);
}

/// <summary>
/// T-911 / FR-A-14: CORS is off until an origin is named. <c>*</c> is refused rather than ignored — with a key on
/// every route, a wildcard invites any page on the internet to spend a browser's credentials here.
/// </summary>
public sealed class CorsSettings
{
    public IList<string> AllowedOrigins { get; set; } = [];
}

/// <summary>
/// T-911 / FR-A-15. The defaults are api-v1's table. <see cref="Enabled"/> exists so the test host can run over a
/// thousand requests through these routes without tripping a brake meant for the internet — it is <b>true</b>
/// everywhere else, including the shipped <c>appsettings.json</c>.
/// </summary>
public sealed class RateLimitSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Requests a minute a caller may send to a route that writes to QuickBooks.</summary>
    public int PostPerMinute { get; set; } = 10;

    /// <summary>Requests a minute a caller may send to any other authenticated route.</summary>
    public int DefaultPerMinute { get; set; } = 120;

    /// <summary>Requests a minute one address may send to the key-free health routes.</summary>
    public int HealthPerMinute { get; set; } = 600;

    /// <summary>Failed authentications a minute from one address before the brute-force brake bites.</summary>
    public int FailedAuthPerMinute { get; set; } = 10;

    /// <summary>How long that address is then refused outright.</summary>
    public int FailedAuthBlockMinutes { get; set; } = 5;
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

    /// <summary>T-910 / FR-A-13: the caller list, git-ignored and holding salted hashes only, never a key.</summary>
    public string Clients { get; set; } = "clients.json";

    /// <summary>
    /// T-908 / §6.3: one folder per direct batch, holding its request, state and qbXML. Blank means "beside the
    /// ledger", which is where the rest of the app's data already lives.
    /// </summary>
    public string ApiBatches { get; set; } = "";

    /// <summary>
    /// T-911 / FR-A-17: the folders a caller's <c>folder</c> may sit inside. Empty means unrestricted, which is only
    /// tolerable while the bind is loopback — <see cref="Security.TransportGuard"/> refuses to start a remote bind
    /// with this list empty, so "unconfigured" can never quietly become "open to strangers".
    /// </summary>
    public IList<string> AllowedJobRoots { get; set; } = [];

    /// <summary>Relative paths resolve against the content root.</summary>
    public void ResolveAgainst(string root)
    {
        Ledger = Path.GetFullPath(Ledger, root);
        QbLists = Path.GetFullPath(QbLists, root);
        Logs = Path.GetFullPath(Logs, root);
        JobIndex = Path.GetFullPath(JobIndex, root);
        Clients = Path.GetFullPath(Clients, root);
        ApiBatches = string.IsNullOrWhiteSpace(ApiBatches)
            ? Path.Combine(Path.GetDirectoryName(Ledger)!, "api-batches")
            : Path.GetFullPath(ApiBatches, root);

        // An allow-list entry that stayed relative would compare against a caller's absolute path and never match,
        // which reads as "the allow-list is broken" rather than "your folder is outside it".
        for (var i = 0; i < AllowedJobRoots.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(AllowedJobRoots[i]))
            {
                AllowedJobRoots[i] = Path.GetFullPath(AllowedJobRoots[i], root);
            }
        }
    }
}
