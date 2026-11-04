namespace QbAutopost.Core.Companies;

public sealed record NewDefaultCompany(string DisplayName, string FilePath, string? FolderName, string? Code);

/// <param name="Backfilled">Rows given the default company: saved_requests, statement_extractions and qb_list_syncs together.</param>
public sealed record DefaultCompanyResult(Company Company, bool Created, int Backfilled);

/// <param name="MatchNames">Replaces the alias list when not null.</param>
/// <param name="FilePath">Must be one of the candidate files; choosing it makes an ambiguous company active.</param>
/// <param name="Disabled">True disables; false re-enables a disabled company: active when its file exists (or
/// <paramref name="FileExists"/> is null), ambiguous when it has no chosen file but has candidates, else missing (ruling B13).</param>
/// <param name="FileExists">Re-enable only: whether the company's file (after this patch) is on disk. The caller checks; the store has no disk.</param>
public sealed record CompanyPatch(IReadOnlyList<string>? MatchNames, string? FilePath, bool? Disabled, bool? FileExists = null);

public enum CompanyChangeKind { Changed, NotFound, Stale, Invalid }

public sealed record CompanyChange(CompanyChangeKind Kind, Company? Company, string? Error = null);

/// <param name="CheckedFilePath">The file the check opened (null when the company had none). The store stamps the result only
/// while the company still points at this file (T-1703 ruling), so a file that was not the one checked is never approved.</param>
public sealed record CompanyAccessCheck(
    CompanyAccess Access, string? Message, string? QbCompanyName, string? QbLegalName, DateTime CheckedUtc, string? CheckedFilePath);

public sealed record ScanApplied(int Added, int Updated, int Missing, int Ambiguous, IReadOnlyList<int> AddedIds, IReadOnlyList<Company> Items);

public interface ICompanyStore
{
    /// <summary>All companies, id order.</summary>
    Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct);

    Task<Company?> GetAsync(int id, CancellationToken ct);

    /// <summary>Creates the default company if the table is empty and, in the same transaction, sets company_id = it
    /// (and saved_requests.company_source = 'migrated') on every saved_requests, statement_extractions and
    /// qb_list_syncs row whose company_id is null. Otherwise returns the existing default, Created false, Backfilled 0.</summary>
    Task<DefaultCompanyResult> EnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct);

    /// <summary>Applies a scan plan, folders matched case-insensitively (ruling B10), and records <c>companies-scanned</c>.</summary>
    Task<ScanApplied> ApplyScanAsync(ScanPlan plan, string actor, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records <c>company-updated</c>.</summary>
    Task<CompanyChange> UpdateAsync(int id, int expectedRowVersion, CompanyPatch patch, string actor, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records <c>company-checked</c>. Stale (nothing stamped, nothing audited) when the company's file is no longer <see cref="CompanyAccessCheck.CheckedFilePath"/>.</summary>
    Task<CompanyChange> RecordAccessAsync(int id, CompanyAccessCheck check, string actor, CancellationToken ct);

    Task<DateTime?> LastScanUtcAsync(CancellationToken ct);
}
