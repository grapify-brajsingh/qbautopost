using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Gates;

namespace QbAutopost.Core.Pipeline;

/// <summary>One company-file check (FR-A-5). <paramref name="Severity"/> decides whether a failure blocks <c>ok</c>.</summary>
public sealed record CompanyFileCheck(string Name, bool Ok, string Severity, string Message);

/// <summary><c>POST /api/v1/quickbooks/company-file/validate</c> body (FR-A-5).</summary>
public sealed record CompanyFileValidation(bool Ok, string? CompanyFile, IReadOnlyList<CompanyFileCheck> Checks, string Message);

/// <summary>
/// FR-A-5: "am I pointed at the right, usable, recently-backed-up company file?" Read-only — it sends no qbXML and
/// posts nothing; the only QuickBooks call is asking which file is open.
/// <para>
/// <c>openInQuickBooks</c> is an <see cref="Error"/> rather than a warning on purpose. QuickBooks having a different
/// company open is the one misconfiguration that silently posts a batch into the wrong company's books.
/// </para>
/// </summary>
public sealed class CompanyFileValidator(PipelineOptions options, IQbGateway gateway)
{
    /// <summary>A failed check that makes the answer not ok.</summary>
    public const string Error = "error";

    /// <summary>A failed check worth reporting that still leaves the answer ok.</summary>
    public const string Warning = "warning";

    private const string Extension = ".QBW";

    public async Task<CompanyFileValidation> ValidateAsync(string? companyFile, CancellationToken ct)
    {
        var checks = new List<CompanyFileCheck>();
        if (string.IsNullOrWhiteSpace(companyFile))
        {
            checks.Add(new CompanyFileCheck("configured", false, Error, "Company:FilePath is not set"));
            return Result(checks, null);
        }

        checks.Add(new CompanyFileCheck("configured", true, Error, "Company:FilePath is set"));

        var path = companyFile.Trim();
        var shaped = Path.IsPathFullyQualified(path)
            && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);
        checks.Add(new CompanyFileCheck(
            "pathShape",
            shaped,
            Error,
            shaped ? $"absolute path, {Extension}" : $"'{path}' must be an absolute path to a {Extension} file"));
        if (!shaped)
        {
            // Every later check would be reporting on a path that cannot be the company file anyway.
            return Result(checks, path);
        }

        var full = Path.GetFullPath(path);
        checks.Add(Exists(full));
        if (checks[^1].Ok)
        {
            checks.Add(Readable(full));
        }

        checks.Add(await OpenInQuickBooksAsync(full, ct));
        checks.Add(Backup());
        checks.Add(ListsSynced());
        return Result(checks, full);
    }

    private static CompanyFileCheck Exists(string path)
    {
        if (!File.Exists(path))
        {
            return new CompanyFileCheck("exists", false, Error, $"{path} does not exist");
        }

        var info = new FileInfo(path);
        return new CompanyFileCheck(
            "exists",
            true,
            Error,
            $"{info.Length / 1024d / 1024d:0.0} MB, modified {info.LastWriteTimeUtc:yyyy-MM-ddTHH:mm:ssZ}");
    }

    private static CompanyFileCheck Readable(string path)
    {
        try
        {
            // QuickBooks holds the file open, so the share flags must allow that; one byte is proof enough.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.ReadByte();
            return new CompanyFileCheck("readable", true, Error, "opened for read");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CompanyFileCheck("readable", false, Error, ex.Message);
        }
    }

    private async Task<CompanyFileCheck> OpenInQuickBooksAsync(string configured, CancellationToken ct)
    {
        string open;
        try
        {
            open = await gateway.CurrentCompanyFileAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new CompanyFileCheck("openInQuickBooks", false, Error, ex.Message);
        }

        return Same(configured, open)
            ? new CompanyFileCheck("openInQuickBooks", true, Error, "QuickBooks has this file open")
            : new CompanyFileCheck("openInQuickBooks", false, Error, $"QuickBooks has a different file open: {open}");
    }

    /// <summary>Windows paths are case-insensitive, and the SDK may spell one differently from the configuration.</summary>
    private static bool Same(string configured, string open)
    {
        if (string.IsNullOrWhiteSpace(open))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(open.Trim())),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// FR-11's own guard, so validating and posting can never disagree about the same backup. A warning here: posting
    /// still refuses outright, and this call must not become the thing that blesses a stale backup.
    /// </summary>
    private CompanyFileCheck Backup()
    {
        if (string.IsNullOrWhiteSpace(options.BackupFolder))
        {
            return new CompanyFileCheck("backupFreshness", true, Warning, "QuickBooks:BackupFolder is not set; backup age is not checked");
        }

        var problem = BackupGuard.Check(options.BackupFolder, options.BackupMaxAgeHours, DateTime.UtcNow);
        return problem is null
            ? new CompanyFileCheck("backupFreshness", true, Warning, $"a .QBB newer than {options.BackupMaxAgeHours} h is present")
            : new CompanyFileCheck("backupFreshness", false, Warning, problem);
    }

    private CompanyFileCheck ListsSynced() =>
        File.Exists(options.QbListsFile)
            ? new CompanyFileCheck("listsSynced", true, Warning, options.QbListsFile)
            : new CompanyFileCheck("listsSynced", false, Warning, $"{options.QbListsFile} not found; run POST /api/v1/quickbooks/lists/sync");

    private static CompanyFileValidation Result(List<CompanyFileCheck> checks, string? companyFile)
    {
        var errors = checks.Count(c => !c.Ok && c.Severity == Error);
        var warnings = checks.Count(c => !c.Ok && c.Severity == Warning);
        var message = errors == 0 && warnings == 0
            ? "the configured company file is the one QuickBooks has open"
            : $"{errors} error(s), {warnings} warning(s)";
        return new CompanyFileValidation(errors == 0, companyFile, checks, message);
    }
}
