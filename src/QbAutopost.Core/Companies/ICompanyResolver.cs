namespace QbAutopost.Core.Companies;

/// <summary>Resolves a company id to its context. Implemented in T-1704.</summary>
public interface ICompanyResolver
{
    CompanyContext Default { get; }
    bool MultiCompany { get; }
    Task<CompanyContext?> GetAsync(int companyId, CancellationToken ct);
    Task<CompanyContext?> AutoAssignAsync(CancellationToken ct);
}
