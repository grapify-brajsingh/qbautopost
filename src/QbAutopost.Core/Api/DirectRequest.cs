using QbAutopost.Core.Models;

namespace QbAutopost.Core.Api;

/// <summary>
/// One transaction a caller sends directly (FR-A-8), instead of it being read off a statement. The fields are what a
/// person would type into <i>Batch Enter Transactions</i>; everything else is derived, so two callers describing the
/// same movement produce the same fingerprint.
/// </summary>
public sealed record DirectRow
{
    /// <summary>The caller's own key. Echoed back and stored, never used to decide identity.</summary>
    public string? ExternalId { get; init; }

    public required TxnKind Kind { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>Positive; the direction comes from <see cref="Kind"/>.</summary>
    public required decimal Amount { get; init; }

    /// <summary>The bank or credit-card account the money moves on.</summary>
    public required string Account { get; init; }

    /// <summary>The expense or income account. Optional: rules (tiers 1–2) resolve it when it is left out.</summary>
    public string? LineAccount { get; init; }

    public string? Payee { get; init; }

    public string? CheckNo { get; init; }

    public string? RefNumber { get; init; }

    public string? Memo { get; init; }

    /// <summary>Last four digits of the account, part of the fingerprint (spec §7).</summary>
    public string? Last4 { get; init; }
}

/// <summary>A direct post (FR-A-8). <see cref="ControlTotal"/> is the caller's own arithmetic, checked never corrected.</summary>
public sealed record DirectRequest
{
    public string? Reference { get; init; }

    public bool? DryRun { get; init; }

    public decimal? ControlTotal { get; init; }

    /// <summary>Tiers 3–4 (Hermes) may choose an account only when this is true and the scope allows it (§6.1).</summary>
    public bool AllowModelAccounts { get; init; }

    public IReadOnlyList<DirectRow> Transactions { get; init; } = [];
}

/// <summary>Bounds from configuration (§6.1, §12), so the reader stays free of settings types.</summary>
public sealed record DirectLimits
{
    public int MaxRows { get; init; } = 500;

    public decimal MaxLineAmount { get; init; } = 100000m;

    public int MaxAgeDays { get; init; } = 730;

    public int MaxFutureDays { get; init; } = 1;
}

/// <summary>A row that will not be posted, with the reason an operator will read.</summary>
public sealed record DirectRowIssue(int Index, string? ExternalId, string Reason);

/// <summary>
/// A row that became a statement line, keeping what the caller said about it. <paramref name="Account"/> is the
/// header account they named: on the direct path it replaces the <c>rules.json</c> last-four lookup the folder path
/// does, so a caller need not edit the rules before posting.
/// </summary>
public sealed record DirectLine(
    int Index, string? ExternalId, TxnKind Kind, string Account, string? LineAccount, string? Payee, StatementLine Line);

/// <summary>
/// The outcome of reading a direct request. <see cref="Errors"/> is a refusal of the whole batch (400 — nothing is
/// posted); <see cref="Held"/> rows are individually not posted, which is the FR-A-8 rule that one bad line must not
/// fail a whole batch.
/// </summary>
public sealed record DirectReadResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<DirectLine> Lines,
    IReadOnlyList<DirectRowIssue> Held);
