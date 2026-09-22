using System.Text.RegularExpressions;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Api;

/// <summary>
/// FR-A-8: turns the rows of a direct request into the same <see cref="StatementLine"/>s a statement produces, so the
/// mapper (FR-6), the duplicate gate (G4), the qbXML builder (FR-9) and verification (G5) are shared with the folder
/// path. One engine, two entrances.
/// <para>
/// What refuses the whole batch and what holds one row is deliberate. A control total that does not add up means the
/// caller and this app disagree about the money, so nothing is posted (CLAUDE.md rule 3 — the total is never
/// "corrected"). A single row that is too large or too old is held instead, so one bad line cannot fail a batch of
/// hundreds (FR-A-8).
/// </para>
/// </summary>
public sealed partial class DirectRequestReader(DirectLimits limits)
{
    public DirectReadResult Read(DirectRequest request, DateOnly today)
    {
        var errors = new List<string>();
        var rows = request.Transactions ?? [];

        if (!string.IsNullOrWhiteSpace(request.Reference) && !ReferencePattern().IsMatch(request.Reference))
        {
            // The reference becomes a batch id and part of a URL (as job ids, T-101).
            errors.Add($"reference '{request.Reference}' may contain only letters, digits, '.', '_' and '-' (1-64 characters)");
        }

        if (rows.Count == 0)
        {
            errors.Add("at least one transaction is required");
        }
        else if (rows.Count > limits.MaxRows)
        {
            errors.Add($"at most {limits.MaxRows} transactions per request, {rows.Count} were sent");
        }

        for (var i = 0; i < rows.Count; i++)
        {
            errors.AddRange(Malformed(rows[i], i));
        }

        if (rows.Count > 0 && errors.Count == 0)
        {
            errors.AddRange(ControlTotal(request, rows));
        }

        if (errors.Count > 0)
        {
            // Nothing is read out of a request that will be refused: partial results invite partial posting.
            return new DirectReadResult(errors, [], []);
        }

        var lines = new List<DirectLine>();
        var held = new List<DirectRowIssue>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (Hold(row, today) is { } reason)
            {
                held.Add(new DirectRowIssue(i, row.ExternalId, reason));
                continue;
            }

            lines.Add(new DirectLine(i, row.ExternalId, row.Kind, Trimmed(row.LineAccount), Trimmed(row.Payee), ToLine(row, request, i)));
        }

        return new DirectReadResult([], lines, held);
    }

    /// <summary>Problems that make the request itself unusable, so the batch is refused rather than trimmed.</summary>
    private static IEnumerable<string> Malformed(DirectRow row, int index)
    {
        if (row.Kind is not (TxnKind.Check or TxnKind.CcCharge or TxnKind.CcCredit or TxnKind.Deposit))
        {
            yield return $"row {index}: kind must be Check, CcCharge, CcCredit or Deposit";
        }

        if (row.Amount <= 0)
        {
            yield return $"row {index}: amount must be greater than zero";
        }
        else if (decimal.Round(row.Amount, 2) != row.Amount)
        {
            yield return $"row {index}: amount must have at most two decimal places";
        }
    }

    /// <summary>
    /// FR-A-9 / §6.1: the caller's arithmetic must match ours to the cent, over every row they sent — including rows
    /// that will be held, because the total describes what they submitted, not what survived.
    /// </summary>
    private static IEnumerable<string> ControlTotal(DirectRequest request, IReadOnlyList<DirectRow> rows)
    {
        if (request.ControlTotal is not { } expected)
        {
            yield return "controlTotal is required when transactions are sent";
            yield break;
        }

        var actual = rows.Sum(r => r.Amount);
        if (actual != expected)
        {
            yield return
                $"control-total-mismatch: the rows add up to {actual:0.00} but controlTotal is {expected:0.00}; " +
                "nothing was posted and no amount was changed";
        }
    }

    /// <summary>Reasons to hold one row. The row is reported, not silently dropped, and never guessed at.</summary>
    private string? Hold(DirectRow row, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(row.Account))
        {
            return "account-missing: the bank or credit-card account is required";
        }

        if (row.Amount > limits.MaxLineAmount)
        {
            return $"amount-over-limit: {row.Amount:0.00} is above the {limits.MaxLineAmount:0.00} limit for one line";
        }

        if (row.Date < today.AddDays(-limits.MaxAgeDays))
        {
            return $"date-too-old: {row.Date:yyyy-MM-dd} is more than {limits.MaxAgeDays} days ago";
        }

        return row.Date > today.AddDays(limits.MaxFutureDays)
            ? $"date-in-the-future: {row.Date:yyyy-MM-dd} is more than {limits.MaxFutureDays} day(s) ahead"
            : null;
    }

    private static StatementLine ToLine(DirectRow row, DirectRequest request, int index) => new()
    {
        // There is no statement, so the source names the request: it appears in logs and the ledger as the origin.
        SourceFile = string.IsNullOrWhiteSpace(request.Reference) ? "api" : $"api:{request.Reference.Trim()}",
        Kind = Source(row.Kind),
        Last4 = Trimmed(row.Last4) ?? string.Empty,
        LineNo = index + 1,
        Date = row.Date,
        Description = Description(row),
        Direction = DirectionOf(row.Kind),
        Amount = row.Amount,
        CheckNo = Trimmed(row.CheckNo),
    };

    /// <summary>Derived, never taken from the caller, so the same movement always fingerprints the same way.</summary>
    private static SourceKind Source(TxnKind kind) =>
        kind is TxnKind.CcCharge or TxnKind.CcCredit ? SourceKind.Card : SourceKind.Bank;

    /// <inheritdoc cref="Source"/>
    private static Direction DirectionOf(TxnKind kind) =>
        kind is TxnKind.Check or TxnKind.CcCharge ? Direction.Debit : Direction.Credit;

    private static string Description(DirectRow row) =>
        Trimmed(row.Memo)
        ?? string.Join(' ', new[] { Trimmed(row.Payee), Trimmed(row.RefNumber) }.Where(p => p is not null));

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ReferencePattern();
}
