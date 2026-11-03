namespace QbAutopost.Core.Companies;

/// <summary>Resolves a company id to its context. Implemented in T-1704.</summary>
public interface ICompanyResolver
{
    CompanyContext Default { get; }
    bool MultiCompany { get; }
    Task<CompanyContext?> GetAsync(int companyId, CancellationToken ct);
    Task<CompanyContext?> AutoAssignAsync(CancellationToken ct);
}

public static class CompanyResolverExtensions
{
    /// <summary>
    /// M17 §4.7: the company a stored statement is validated and posted against. A statement with no company is the
    /// default company (TRANSITIONAL until T-1712: it becomes a <c>company-required</c> hold). Null when the statement
    /// names a company that no longer exists (ruling B18: answered <c>company-not-ready</c>).
    /// </summary>
    public static async Task<CompanyContext?> ForStatementAsync(this ICompanyResolver resolver, int? companyId, CancellationToken ct) =>
        companyId is { } id ? await resolver.GetAsync(id, ct) : resolver.Default; // T-1712: company-required hold
}
