using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Endpoints.V2;

public sealed record CompanyRefView(int Id, string? Code, string DisplayName);

/// <summary>When the company's QuickBooks lists were last synced and how the newest attempt ended; null on the company when never synced.</summary>
public sealed record CompanyListsView(DateTime? SyncedUtc, string? Outcome);

public sealed record CompanyView(
    int Id,
    string? Code,
    string DisplayName,
    string? FolderName,
    string? FileName,
    string? FilePath,
    string State,
    IReadOnlyList<string> CandidateFiles,
    string Access,
    DateTime? AccessCheckedUtc,
    string? AccessMessage,
    string? QbCompanyName,
    string? QbLegalName,
    IReadOnlyList<string> MatchNames,
    bool IsDefault,
    int RowVersion,
    CompanyListsView? Lists,
    int WaitingCount);

public sealed record CompaniesView(
    string RootFolder, bool RootConfigured, bool RootReachable, DateTime? LastScanUtc, IReadOnlyList<CompanyView> Items);

/// <summary>
/// <c>updated</c> counts the companies whose stored values changed; a company that reads the same after the scan
/// (a multi-file company with its file chosen, say) is not counted. <c>items</c> are all companies after the scan.
/// </summary>
public sealed record CompanyScanView(
    int Added, int Updated, int Missing, int Ambiguous, IReadOnlyList<string> NoCompanyFile, IReadOnlyList<CompanyView> Items);

/// <summary>
/// M17 §4.2, §5.1: the company list (<c>lists:read</c>), the folder scan and the admin's edits
/// (<c>requests:admin</c>, audited as <c>companies-scanned</c> and <c>company-updated</c>).
/// </summary>
public static class CompanyEndpoints
{
    public const int MaxMatchNames = 50;
    public const int MaxMatchNameLength = 200;

    private static readonly string[] PatchFields = ["matchNames", "filePath", "disabled"];

    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder v2)
    {
        v2.MapGet("/companies", ListAsync).RequireDatabaseWhenConfigured();
        v2.MapPost("/companies/scan", ScanAsync).RequireDatabaseWhenConfigured();
        v2.MapPatch("/companies/{id}", PatchAsync).RequireDatabaseWhenConfigured();
        return v2;
    }

    private static async Task<IResult> ListAsync(
        IOptions<AppSettings> settings, CompanyResolver resolver, IQbListsSource lists, IServiceProvider services, CancellationToken ct)
    {
        var s = settings.Value;
        var store = services.GetService<ICompanyStore>();
        var root = s.Companies.RootFolder;
        var views = new CompanyViews(lists, store is null ? new Dictionary<int, int>() : await PendingAsync(services, ct));
        IReadOnlyList<CompanyView> items = store is null
            ? [views.OfDefault(resolver.Default, s)]
            : [.. (await store.ListAsync(ct)).Select(views.Of)];
        var lastScan = store is null ? null : await store.LastScanUtcAsync(ct);
        return Results.Ok(new CompaniesView(root, s.Companies.Configured, s.Companies.Configured && Directory.Exists(root), lastScan, items));
    }

    /// <summary>Reads the whole root first and applies only when it could be read, so a bad root changes nothing.</summary>
    private static async Task<IResult> ScanAsync(
        HttpContext http, IOptions<AppSettings> settings, CompanyFolderScanner scanner, CompanyDataFolder folders,
        IClock clock, IQbListsSource lists, IServiceProvider services, ILoggerFactory loggers, CancellationToken ct)
    {
        var root = settings.Value.Companies.RootFolder;
        if (string.IsNullOrWhiteSpace(root))
        {
            return CompanyProblems.RootUnavailable("Companies:RootFolder is not set");
        }

        if (services.GetService<ICompanyStore>() is not { } store)
        {
            return CompanyProblems.RootUnavailable("scanning needs the database");
        }

        ScanResult scan;
        try
        {
            scan = scanner.Scan(root);
        }
        catch (CompanyRootUnavailableException ex)
        {
            return CompanyProblems.RootUnavailable(ex.RootFolder);
        }

        var known = (await store.ListAsync(ct)).Where(c => !string.IsNullOrEmpty(c.FolderName))
            .Select(c => new KnownCompanyFolder(c.Id, c.FolderName!, c.FilePath, c.State, c.IsDefault)).ToList();
        var plan = CompanyFolderScanner.Reconcile(scan, known);
        var applied = await store.ApplyScanAsync(plan, CallerMiddleware.CallerOf(http)!.Actor, clock.UtcNow, ct);
        var companies = await store.ListAsync(ct);
        PrepareFolders(companies, folders, loggers.CreateLogger(typeof(CompanyEndpoints)));
        var views = new CompanyViews(lists, await PendingAsync(services, ct));
        var items = companies.Select(views.Of).ToList();
        return Results.Ok(new CompanyScanView(applied.Added, applied.Updated, applied.Missing, applied.Ambiguous, plan.NoCompanyFile, items));
    }

    /// <summary>
    /// Gives every scan-created company its rules file and ledger (idempotent: only what is absent is created), so a
    /// failure on one scan (template missing, disk full) is retried by the next. A failure is logged, never fatal: the
    /// companies are already stored and the scan has to answer.
    /// </summary>
    private static void PrepareFolders(IReadOnlyList<Company> companies, CompanyDataFolder folders, ILogger log)
    {
        foreach (var company in companies.Where(c => !c.IsDefault))
        {
            try
            {
                folders.Prepare(company.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not prepare the data folder of company {CompanyId}; the next scan retries", company.Id);
            }
        }
    }

    private static async Task<IResult> PatchAsync(
        HttpContext http, string id, IClock clock, IQbListsSource lists, IServiceProvider services, CancellationToken ct)
    {
        if (SavedRequestEndpoints.IfMatchOf(http) is not { } expected)
        {
            return CompanyProblems.PreconditionRequired();
        }

        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var companyId))
        {
            return CompanyProblems.NotFound(0);
        }

        if (services.GetService<ICompanyStore>() is not { } store)
        {
            return CompanyProblems.RootUnavailable("editing companies needs the database");
        }

        var (patch, error) = await ReadPatchAsync(http, ct);
        if (error is not null)
        {
            return error;
        }

        var current = await store.GetAsync(companyId, ct);
        if (current is null)
        {
            return CompanyProblems.NotFound(companyId);
        }

        // Re-enabling needs to know whether the company's file is on disk (ruling B13); the store has no disk.
        if (patch!.Disabled == false)
        {
            var path = patch.FilePath ?? current.FilePath;
            patch = patch with { FileExists = path is not null && File.Exists(path) };
        }

        var change = await store.UpdateAsync(companyId, expected, patch, CallerMiddleware.CallerOf(http)!.Actor, clock.UtcNow, ct);
        switch (change.Kind)
        {
            case CompanyChangeKind.NotFound:
                return CompanyProblems.NotFound(companyId);
            case CompanyChangeKind.Stale:
                return CompanyProblems.EditedBySomeoneElse(change.Company!.RowVersion);
            case CompanyChangeKind.Invalid:
                return CompanyProblems.InvalidRequest(change.Error ?? "The change is not allowed.");
            default:
                var views = new CompanyViews(lists, await PendingAsync(services, ct));
                http.Response.Headers.ETag = $"\"{change.Company!.RowVersion.ToString(CultureInfo.InvariantCulture)}\"";
                return Results.Ok(views.Of(change.Company));
        }
    }

    /// <summary>
    /// The body is an object with at least one of <c>matchNames</c> (array of strings: trimmed, blanks dropped, at most
    /// 50 of at most 200 characters), <c>filePath</c> (string) and <c>disabled</c> (boolean). Anything else is 400.
    /// </summary>
    private static async Task<(CompanyPatch? Patch, IResult? Error)> ReadPatchAsync(HttpContext http, CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException)
        {
            return (null, CompanyProblems.InvalidRequest("The body must be a JSON object."));
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return (null, CompanyProblems.InvalidRequest("The body must be a JSON object."));
        }

        IReadOnlyList<string>? names = null;
        string? filePath = null;
        bool? disabled = null;
        foreach (var property in body.EnumerateObject())
        {
            switch (PatchFields.FirstOrDefault(f => f.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                case "matchNames":
                    if (!TryNames(property.Value, out names, out var problem))
                    {
                        return (null, CompanyProblems.InvalidRequest(problem));
                    }

                    break;
                case "filePath":
                    if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        return (null, CompanyProblems.InvalidRequest("filePath must be a non-empty string."));
                    }

                    filePath = property.Value.GetString();
                    break;
                case "disabled":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return (null, CompanyProblems.InvalidRequest("disabled must be true or false."));
                    }

                    disabled = property.Value.GetBoolean();
                    break;
                default:
                    return (null, CompanyProblems.InvalidRequest($"Unknown field '{property.Name}'. The fields are {string.Join(", ", PatchFields)}."));
            }
        }

        return names is null && filePath is null && disabled is null
            ? (null, CompanyProblems.InvalidRequest("Send at least one of matchNames, filePath, disabled."))
            : (new CompanyPatch(names, filePath, disabled), null);
    }

    private static bool TryNames(JsonElement value, out IReadOnlyList<string>? names, out string problem)
    {
        names = null;
        problem = "matchNames must be an array of strings.";
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var cleaned = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = entry.GetString()!.Trim();
            if (name.Length > MaxMatchNameLength)
            {
                problem = $"A match name may be at most {MaxMatchNameLength} characters.";
                return false;
            }

            if (name.Length > 0)
            {
                cleaned.Add(name);
            }
        }

        if (cleaned.Count > MaxMatchNames)
        {
            problem = $"At most {MaxMatchNames} match names are allowed.";
            return false;
        }

        names = cleaned;
        return true;
    }

    private static async Task<IReadOnlyDictionary<int, int>> PendingAsync(IServiceProvider services, CancellationToken ct) =>
        await services.GetRequiredService<ISavedRequestV2Store>().PendingByCompanyAsync(ct);

    /// <summary>Maps a stored company to its view: the file name, the default company's lists and the waiting count.</summary>
    private sealed class CompanyViews(IQbListsSource lists, IReadOnlyDictionary<int, int> pending)
    {
        public CompanyView Of(Company c) => new(
            c.Id, c.Code, c.DisplayName, c.FolderName, c.FilePath is null ? null : Path.GetFileName(c.FilePath), c.FilePath,
            CompanyText.Of(c.State), c.CandidateFiles, CompanyText.Of(c.Access), c.AccessCheckedUtc, c.AccessMessage,
            c.QbCompanyName, c.QbLegalName, c.MatchNames, c.IsDefault, c.RowVersion,
            c.IsDefault ? ListsOfDefault() : null, // T-1709: per-company lists
            pending.GetValueOrDefault(c.Id));

        /// <summary>Without a database there is only the default company, built from <c>Company:*</c>.</summary>
        public CompanyView OfDefault(CompanyContext c, AppSettings s) => new(
            c.Id, null, c.DisplayName, null, Path.GetFileName(s.Company.FilePath), s.Company.FilePath,
            CompanyText.Of(CompanyState.Active), [], CompanyText.Of(CompanyAccess.Approved), null, null, null, null, [], true, 0,
            ListsOfDefault(), 0);

        private CompanyListsView? ListsOfDefault() =>
            lists.Current.SyncedUtc is { } synced
                ? new CompanyListsView(synced, lists.LastAttempt?.Outcome.ToString().ToLowerInvariant())
                : null;
    }
}
