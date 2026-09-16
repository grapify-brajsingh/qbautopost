using QbAutopost.Core.Models;

namespace QbAutopost.Core.Gates;

/// <summary>Result of gate G1 for one statement (spec FR-4).</summary>
public sealed record ReconcileResult(bool Ok, bool Verified, string Message)
{
    public const string NotVerifiable = "not-verifiable";
}

/// <summary>
/// Gate G1 — running-balance chain (spec FR-4). A statement that fails is held whole.
/// "Both orientations" = the rows may be listed oldest-first or newest-first.
/// SPEC-GAP T-002: the balance sign is fixed by the statement kind (bank: credits raise the balance;
/// card: charges raise the amount owed) so a wrongly configured amount sign can never pass the gate.
/// A balance column that is only partly filled fails the gate.
/// </summary>
public static class ReconcileGate
{
    public const decimal Tolerance = 0.01m;

    public static ReconcileResult CheckBalanceChain(IReadOnlyList<StatementLine> rows)
    {
        var withBalance = rows.Count(r => r.Balance.HasValue);
        if (rows.Count == 0 || withBalance == 0)
        {
            return new ReconcileResult(true, false, ReconcileResult.NotVerifiable);
        }

        if (withBalance != rows.Count)
        {
            return new ReconcileResult(false, false, $"balance missing on {rows.Count - withBalance} of {rows.Count} rows");
        }

        if (rows.Count == 1)
        {
            return new ReconcileResult(true, false, $"{ReconcileResult.NotVerifiable}: single row");
        }

        if (FirstBreak(rows) is null)
        {
            return new ReconcileResult(true, true, "balance chain reconciles (file order)");
        }

        var reversed = rows.Reverse().ToList();
        if (FirstBreak(reversed) is null)
        {
            return new ReconcileResult(true, true, "balance chain reconciles (reverse order)");
        }

        return new ReconcileResult(
            false, true, $"balance chain does not reconcile in either order (file order breaks at line {FirstBreak(rows)!.LineNo})");
    }

    /// <summary>First row whose balance is not the previous balance plus its own signed amount; null when the chain holds.</summary>
    private static StatementLine? FirstBreak(IReadOnlyList<StatementLine> ordered)
    {
        for (var i = 1; i < ordered.Count; i++)
        {
            var expected = ordered[i - 1].Balance!.Value + BalanceEffect(ordered[i]);
            if (Math.Abs(expected - ordered[i].Balance!.Value) > Tolerance)
            {
                return ordered[i];
            }
        }

        return null;
    }

    private static decimal BalanceEffect(StatementLine line)
    {
        var raisesBalance = line.Kind == SourceKind.Bank
            ? line.Direction == Direction.Credit
            : line.Direction == Direction.Debit;
        return raisesBalance ? line.Amount : -line.Amount;
    }
}
