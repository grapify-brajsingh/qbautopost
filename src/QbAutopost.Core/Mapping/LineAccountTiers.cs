using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Mapping;

public sealed record TierResult(string Account, Confidence Confidence, int Tier);

/// <summary>
/// Spec FR-6 line-account tiers 1 (rule) and 2 (ledger history).
/// TODO(T-502): tiers 3 (invoice hint) and 4 (Hermes T4) are added in M5.
/// </summary>
public sealed class LineAccountTiers(Rules rules, IReadOnlyList<LedgerEntry> history)
{
    private const int DominanceFactor = 3;

    public TierResult? Resolve(string payee, string normalizedDescription)
    {
        if (rules.VendorAccounts.TryGetValue(payee, out var vendorAccount) && !string.IsNullOrWhiteSpace(vendorAccount))
        {
            return new TierResult(vendorAccount, Confidence.Rule, 1);
        }

        return KeywordRule(normalizedDescription) ?? FromHistory(payee);
    }

    public TierResult? KeywordRule(string normalizedDescription)
    {
        var keyword = rules.KeywordAccounts.FirstOrDefault(k => k.IsMatch(normalizedDescription));
        return keyword is null ? null : new TierResult(keyword.Account, Confidence.Rule, 1);
    }

    /// <summary>≥ HistoryMinCount postings for the payee and the top account used ≥ 3× the runner-up.</summary>
    private TierResult? FromHistory(string payee)
    {
        var counts = history
            .Where(e => !e.Undone
                        && string.Equals(e.Payee, payee, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.LineAccount))
            .GroupBy(e => e.LineAccount!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Account: g.Key, Count: g.Count()))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Account, StringComparer.Ordinal)
            .ToList();

        if (counts.Sum(c => c.Count) < rules.HistoryMinCount)
        {
            return null;
        }

        var runnerUp = counts.Count > 1 ? counts[1].Count : 0;
        return counts[0].Count >= DominanceFactor * runnerUp
            ? new TierResult(counts[0].Account, Confidence.History, 2)
            : null;
    }
}
