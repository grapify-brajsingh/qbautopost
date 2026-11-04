using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Companies;

/// <summary>
/// Start-up (spec §4.4): makes sure the default company exists — built from <c>Company:*</c> — and that every old row
/// belongs to it. Copies no file: the default company keeps <c>Company:RulesFile</c> and <c>Paths:Ledger</c>.
/// </summary>
public sealed class CompanyBootstrap(ICompanyStore store, IOptions<AppSettings> settings, CompanyResolver resolver, IClock clock, ILogger<CompanyBootstrap> log)
{
    public async Task<DefaultCompanyResult> RunAsync(CancellationToken ct)
    {
        var s = settings.Value;
        var (folderName, code) = FolderOf(s.Companies.RootFolder, s.Company.FilePath);
        var name = string.IsNullOrWhiteSpace(s.Company.Name) ? "Default company" : s.Company.Name;
        var result = await store.EnsureDefaultAsync(new NewDefaultCompany(name, s.Company.FilePath, folderName, code), clock.UtcNow, ct);
        resolver.UseDefaultId(result.Company.Id);
        if (result.Created)
        {
            log.LogInformation("Default company {Id} created; {Count} existing row(s) assigned to it", result.Company.Id, result.Backfilled);
        }

        return result;
    }

    /// <summary>
    /// Folder name and code only when the file sits exactly one folder below the root (as a scan would find it); full
    /// paths, case-insensitive. The scan uses it too, to adopt a default company bootstrapped before the root was set.
    /// </summary>
    public static (string? FolderName, string? Code) FolderOf(string rootFolder, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return (null, null);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var parent = folder is null ? null : Path.GetDirectoryName(folder);
        if (folder is null || parent is null || !string.Equals(Trim(parent), Trim(Path.GetFullPath(rootFolder)), StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var folderName = Path.GetFileName(folder);
        return (folderName, CompanyFolderScanner.ParseFolderName(folderName).Code);
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
