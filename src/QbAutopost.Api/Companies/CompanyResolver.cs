using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Companies;

/// <summary>
/// Turns a company id into the files a post or sync uses. The default company keeps the configured
/// <c>Company:*</c>/<c>Paths:Ledger</c> files; every other company uses <see cref="CompanyDataFolder"/>.
/// Without a database or without <c>Companies:RootFolder</c> there is one company: the default.
/// </summary>
public sealed class CompanyResolver(IOptions<AppSettings> settings, CompanyDataFolder folders, ICompanyStore? store = null) : ICompanyResolver
{
    private int _defaultId;

    public void UseDefaultId(int id) => Volatile.Write(ref _defaultId, id);

    public bool MultiCompany => settings.Value.Companies.Configured && store is not null;

    public CompanyContext Default
    {
        get
        {
            var s = settings.Value;
            return new CompanyContext(Volatile.Read(ref _defaultId), s.Company.Name, s.Company.FilePath, s.Company.RulesFile, s.Paths.Ledger)
            {
                IsDefault = true,
            };
        }
    }

    public async Task<CompanyContext?> GetAsync(int companyId, CancellationToken ct)
    {
        if (store is null)
        {
            return companyId == Volatile.Read(ref _defaultId) ? Default : null;
        }

        var company = await store.GetAsync(companyId, ct);
        return company is null ? null : ContextOf(company);
    }

    public async Task<CompanyContext?> AutoAssignAsync(CancellationToken ct)
    {
        if (!MultiCompany)
        {
            return Default;
        }

        var active = (await store!.ListAsync(ct)).Where(c => c.State == CompanyState.Active).Take(2).ToList();
        return active.Count == 1 ? ContextOf(active[0]) : null;
    }

    // The one place a row becomes a context, so T-1708's guard (file under Companies:RootFolder, or equal to the default
    // Company:FilePath) fits here without touching callers. A non-default company with no file gets "" and carries its
    // state, so callers refuse it.
    private CompanyContext ContextOf(Company company)
    {
        var context = company.IsDefault
            ? Default with { Id = company.Id }
            : new CompanyContext(company.Id, company.DisplayName, company.FilePath ?? "", folders.RulesFileOf(company.Id), folders.LedgerFileOf(company.Id));
        return context with { State = company.State, Access = company.Access };
    }
}
