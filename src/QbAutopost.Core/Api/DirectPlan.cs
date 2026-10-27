using QbAutopost.Core.Models;

namespace QbAutopost.Core.Api;

/// <summary>What a direct row would do if the batch were posted now (FR-A-6).</summary>
public enum DirectOutcome
{
    /// <summary>The row is mapped, gated and ready; only the SDK call is missing.</summary>
    WouldPost,

    /// <summary>The row is not posted and a person must decide. <see cref="DirectPlanRow.Reason"/> says why.</summary>
    Held,

    /// <summary>A rule (FR-6 skip patterns) says this row is never posted.</summary>
    Skipped,

    /// <summary>The ledger already has this fingerprint: posting again would double it (G4).</summary>
    Duplicate,
}

/// <summary>One submitted row and the decision the planner reached about it.</summary>
public sealed record DirectPlanRow
{
    public required int Index { get; init; }

    public string? ExternalId { get; init; }

    /// <summary>Spec §7 <c>fingerprint[..16]</c>; null for a row refused before it became a line.</summary>
    public string? RequestId { get; init; }

    public required DirectOutcome Outcome { get; init; }

    public TxnKind? Kind { get; init; }

    /// <summary>The bank or credit-card account, as the caller named it.</summary>
    public string? Account { get; init; }

    public string? LineAccount { get; init; }

    public string? Payee { get; init; }

    public required decimal Amount { get; init; }

    public Confidence? Confidence { get; init; }

    /// <summary>A <see cref="HoldReasons"/> code, or a reader message for a row that never became a line.</summary>
    public string? Reason { get; init; }

    public string? Note { get; init; }

    public IReadOnlyList<string> Candidates { get; init; } = [];

    /// <summary>For a duplicate: the batch that posted it, so an operator can look it up.</summary>
    public string? BatchId { get; init; }
}

/// <summary>Names the request uses that <c>qb-lists.json</c> does not have (FR-A-6).</summary>
public sealed record DirectUnknownNames(
    IReadOnlyList<string> Accounts,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> Customers)
{
    public static DirectUnknownNames None { get; } = new([], [], []);
}

/// <summary>
/// The result of planning a direct request (FR-A-6): every decision the post will make except the SDK call itself.
/// <para>
/// <see cref="Errors"/> means the request was refused whole (400) and nothing was planned out of it; row-level
/// outcomes mean the request was understood and a gate spoke (422 unless <see cref="Ok"/>).
/// </para>
/// </summary>
public sealed record DirectPlan
{
    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<DirectPlanRow> Rows { get; init; } = [];

    /// <summary>The mapped transactions behind the <see cref="DirectOutcome.WouldPost"/> rows, in submitted order.</summary>
    public IReadOnlyList<MappedTxn> ToPost { get; init; } = [];

    /// <summary>The FR-9 request for <see cref="ToPost"/>; empty when nothing would post.</summary>
    public string QbXml { get; init; } = string.Empty;

    /// <summary>qbXML request element name (<c>CheckAdd</c>, …) → how many of them, which is all FR-A-6 reports by default.</summary>
    public IReadOnlyDictionary<string, int> QbXmlCounts { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public DirectUnknownNames UnknownNames { get; init; } = DirectUnknownNames.None;

    public int Submitted { get; init; }

    public decimal SubmittedTotal { get; init; }

    public decimal WouldPostTotal { get; init; }

    public decimal? ControlTotal { get; init; }

    public int WouldPost => Count(DirectOutcome.WouldPost);

    public int Held => Count(DirectOutcome.Held);

    public int Duplicates => Count(DirectOutcome.Duplicate);

    /// <summary>A duplicate is a skip with a known cause, so this counts both and the four counts add up to <see cref="Submitted"/>.</summary>
    public int Skipped => Count(DirectOutcome.Skipped) + Duplicates;

    /// <summary>
    /// True when the batch would post cleanly. A skip is a rule doing its job and leaves the batch ok; a hold or a
    /// duplicate needs a person, so it does not.
    /// </summary>
    public bool Ok => Errors.Count == 0 && Held == 0 && Duplicates == 0;

    private int Count(DirectOutcome outcome) => Rows.Count(r => r.Outcome == outcome);
}
