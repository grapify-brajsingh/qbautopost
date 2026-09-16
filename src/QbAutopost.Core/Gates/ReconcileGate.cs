using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;
using QbAutopost.Core.Text;

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

    /// <summary>
    /// G1 for Hermes T2 rows (spec FR-4): <c>|opening + Σeffects − closing| ≤ 0.01</c>, <c>rows == transactionCount</c>
    /// when printed, every date inside the printed period, and any printed running balances consistent (either order).
    /// SPEC-GAP T-304: without both opening and closing balance a T2 statement is not verifiable and fails — model-read
    /// amounts are never posted unchecked. The effect sign follows the kind as in <see cref="CheckBalanceChain"/>.
    /// </summary>
    public static ReconcileResult CheckExtraction(SourceKind kind, IReadOnlyList<StatementLine> rows, StatementTotals totals)
    {
        if (totals.OpeningBalance is not { } opening || totals.ClosingBalance is not { } closing)
        {
            return new ReconcileResult(
                false, false, $"{ReconcileResult.NotVerifiable}: the statement text gave no opening and closing balance");
        }

        var failures = new List<string>();
        var expected = opening + rows.Sum(r => BalanceEffect(kind, r));
        if (Math.Abs(expected - closing) > Tolerance)
        {
            failures.Add($"opening {Money.Format(opening)} with the rows gives {Money.Format(expected)}, closing is {Money.Format(closing)}");
        }

        if (totals.TransactionCount is { } count && count != rows.Count)
        {
            failures.Add($"{rows.Count} rows read, statement prints {count} transactions");
        }

        var outside = rows
            .Where(r => r.Date < totals.PeriodStart || r.Date > totals.PeriodEnd)
            .Select(r => $"line {r.LineNo} {r.Date:yyyy-MM-dd}")
            .ToList();
        if (outside.Count > 0)
        {
            failures.Add($"dates outside the statement period {totals.PeriodStart:yyyy-MM-dd}…{totals.PeriodEnd:yyyy-MM-dd}: {string.Join(", ", outside)}");
        }

        if (FirstPartialBreak(kind, rows) is { } fileOrderBreak && FirstPartialBreak(kind, rows.Reverse().ToList()) is not null)
        {
            failures.Add($"running balance does not match the amounts in either order (file order breaks at line {fileOrderBreak.LineNo})");
        }

        return failures.Count == 0
            ? new ReconcileResult(true, true, "opening and closing balance reconcile with the rows")
            : new ReconcileResult(false, true, string.Join("; ", failures));
    }

    /// <summary>
    /// Like <see cref="FirstBreak"/> but for statements that print a balance on some rows only: each printed balance must
    /// equal the previous printed balance plus the rows in between (including its own row).
    /// </summary>
    private static StatementLine? FirstPartialBreak(SourceKind kind, IReadOnlyList<StatementLine> ordered)
    {
        decimal? anchor = null;
        foreach (var row in ordered)
        {
            if (anchor is not null)
            {
                anchor += BalanceEffect(kind, row);
            }

            if (row.Balance is not { } balance)
            {
                continue;
            }

            if (anchor is not null && Math.Abs(anchor.Value - balance) > Tolerance)
            {
                return row;
            }

            anchor = balance;
        }

        return null;
    }

    /// <summary>First row whose balance is not the previous balance plus its own signed amount; null when the chain holds.</summary>
    private static StatementLine? FirstBreak(IReadOnlyList<StatementLine> ordered)
    {
        for (var i = 1; i < ordered.Count; i++)
        {
            var expected = ordered[i - 1].Balance!.Value + BalanceEffect(ordered[i].Kind, ordered[i]);
            if (Math.Abs(expected - ordered[i].Balance!.Value) > Tolerance)
            {
                return ordered[i];
            }
        }

        return null;
    }

    private static decimal BalanceEffect(SourceKind kind, StatementLine line)
    {
        var raisesBalance = kind == SourceKind.Bank
            ? line.Direction == Direction.Credit
            : line.Direction == Direction.Debit;
        return raisesBalance ? line.Amount : -line.Amount;
    }
}
