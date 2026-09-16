using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

/// <summary>Outcome of payee resolution: a name, or no name plus up to three candidates for a person to pick from.</summary>
public sealed record PayeeResolution(string? Name, IReadOnlyList<string> Candidates, string? Note)
{
    public static PayeeResolution Found(string name) => new(name, [], null);
}

/// <summary>
/// Spec FR-6 payee resolution: alias table (substring on the normalised description) → fuzzy match
/// against known names (QuickBooks lists, rule keys, ledger payees) at or above <see cref="Rules.FuzzyThreshold"/>.
/// </summary>
public sealed class PayeeResolver
{
    private const int CandidateCount = 3;

    private readonly Rules _rules;
    private readonly IReadOnlyList<string> _vendorNames;
    private readonly IReadOnlyList<string> _customerNames;

    public PayeeResolver(Rules rules, QbLists lists, IReadOnlyList<LedgerEntry> history)
    {
        _rules = rules;
        var live = history.Where(e => !e.Undone && !string.IsNullOrWhiteSpace(e.Payee)).ToList();
        _vendorNames = lists.Vendors
            .Concat(rules.VendorAccounts.Keys)
            .Concat(live.Where(e => e.Kind != TxnKind.Deposit).Select(e => e.Payee!))
            .ToList();
        _customerNames = lists.Customers
            .Concat(live.Where(e => e.Kind == TxnKind.Deposit).Select(e => e.Payee!))
            .ToList();
    }

    public PayeeResolution ResolveVendor(string description) => Resolve(description, _rules.PayeeAliases, _vendorNames);

    public PayeeResolution ResolveCustomer(string description) => Resolve(description, _rules.CustomerAliases, _customerNames);

    private PayeeResolution Resolve(string description, IReadOnlyDictionary<string, string> aliases, IReadOnlyList<string> names)
    {
        var normalized = TextNormalizer.Normalize(description);
        var aliasHits = aliases
            .Where(a => !string.IsNullOrWhiteSpace(a.Value) && TextNormalizer.ContainsFragment(normalized, a.Key))
            .Select(a => a.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (aliasHits.Count == 1)
        {
            return PayeeResolution.Found(aliasHits[0]);
        }

        if (aliasHits.Count > 1)
        {
            // SPEC-GAP T-002: several aliases point to different names → hold instead of picking one.
            return new PayeeResolution(null, aliasHits.Take(CandidateCount).ToList(), "ambiguous alias match");
        }

        var ranked = Fuzzy.Rank(description, names, CandidateCount);
        var candidates = ranked.Select(h => h.Name).ToList();
        var above = ranked.Where(h => h.Score >= _rules.FuzzyThreshold).ToList();
        if (above.Count == 0)
        {
            return new PayeeResolution(null, candidates, null);
        }

        if (above.Count > 1 && above[0].Score.Equals(above[1].Score))
        {
            // SPEC-GAP T-002: equal best fuzzy scores → hold instead of picking by name order.
            return new PayeeResolution(null, candidates, "ambiguous fuzzy match");
        }

        return PayeeResolution.Found(above[0].Name);
    }
}
