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
    /// M17 §4.6, §4.7: the company a stored statement is validated and posted against, read from its stored row (the
    /// default company included, so its readiness is the row's). Null when the statement has no company (callers answer
    /// <c>company-required</c> before asking) or names one that no longer exists (ruling B18: <c>company-not-ready</c>).
    /// </summary>
    public static async Task<CompanyContext?> ForStatementAsync(this ICompanyResolver resolver, int? companyId, CancellationToken ct) =>
        companyId is { } id ? await resolver.GetAsync(id, ct) : null;
}
