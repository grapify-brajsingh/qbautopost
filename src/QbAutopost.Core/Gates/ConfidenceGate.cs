using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Gates;

/// <summary>
/// Gate G3 (spec FR-7) for accounts chosen by Hermes T4. Rule, History and Holding lines always post; an Invoice-tier
/// answer posts at or above the threshold; a Model-tier answer also needs a prior posting of the payee to that account.
/// </summary>
public static class ConfidenceGate
{
    public static bool InvoiceMayPost(double score, double threshold) => score >= threshold;

    /// <summary>
    /// Null when a Model-tier line may post, else the hold reason.
    /// SPEC-GAP T-502: FR-7 names no codes; below the threshold → <c>low-confidence</c> (checked first), no live ledger
    /// posting of the payee to the account → <c>no-prior-posting</c>. Names compare ignoring case, as in tier 2.
    /// </summary>
    public static string? CheckModel(
        double score, string payee, string account, double threshold, IReadOnlyList<LedgerEntry> history)
    {
        if (score < threshold)
        {
            return HoldReasons.LowConfidence;
        }

        var postedBefore = history.Any(e => !e.Undone
            && string.Equals(e.Payee, payee, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.LineAccount, account, StringComparison.OrdinalIgnoreCase));
        return postedBefore ? null : HoldReasons.NoPriorPosting;
    }
}
