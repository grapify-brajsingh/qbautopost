using QbAutopost.Api.Endpoints.V2;
using QbAutopost.Core.Companies;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// M17 spec 5.2: what one v2 answer needs to know about the companies of its statements: each company's
/// <c>{ id, code, displayName }</c> (one <see cref="ICompanyStore.ListAsync"/> per answer) and whether it may be validated
/// and posted (<see cref="CompanyGate"/>, through the resolver, so readiness is decided exactly as validate and post decide it).
/// </summary>
public sealed class StatementCompanies
{
    private readonly Dictionary<int, CompanyRefView> _refs;
    private readonly Dictionary<int, CompanyGate> _gates;

    private StatementCompanies(Dictionary<int, CompanyRefView> refs, Dictionary<int, CompanyGate> gates)
    {
        _refs = refs;
        _gates = gates;
    }

    /// <param name="companyIds">The companies of the statements in the answer; nulls and repeats are fine.</param>
    /// <param name="store">Null on a host without a database: the default company is then the only one.</param>
    public static async Task<StatementCompanies> LoadAsync(
        IEnumerable<int?> companyIds, ICompanyStore? store, ICompanyResolver resolver, CancellationToken ct)
    {
        var rows = store is null ? [] : await store.ListAsync(ct);
        var refs = rows.ToDictionary(c => c.Id, c => new CompanyRefView(c.Id, c.Code, c.DisplayName));
        var gates = new Dictionary<int, CompanyGate>();
        foreach (var id in companyIds.OfType<int>().Distinct())
        {
            var company = await resolver.GetAsync(id, ct);
            gates[id] = company is { CanOpenSession: true } ? CompanyGate.Ready : CompanyGate.NotReady;
            if (company is not null && !refs.ContainsKey(id))
            {
                refs[id] = new CompanyRefView(id, null, company.DisplayName);
            }
        }

        return new StatementCompanies(refs, gates);
    }

    /// <summary>The company's reference, or null when the statement has none (or it no longer exists).</summary>
    public CompanyRefView? RefOf(int? companyId) => companyId is { } id ? _refs.GetValueOrDefault(id) : null;

    /// <summary><see cref="CompanyGate.Required"/> without a company; <see cref="CompanyGate.NotReady"/> for one unknown here.</summary>
    public CompanyGate GateOf(int? companyId) =>
        companyId is { } id ? _gates.GetValueOrDefault(id, CompanyGate.NotReady) : CompanyGate.Required;

    /// <summary>The wire name of a source (<c>matched</c>, <c>chosen</c>, <c>migrated</c>, <c>default</c>), or null.</summary>
    public static string? SourceName(CompanySource? source) => source is { } s ? CompanyText.Of(s) : null;
}
