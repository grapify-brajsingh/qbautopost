using System.Text.Json.Serialization;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Pipeline;

/// <summary>One statement file after extraction and G1 (spec FR-3/FR-4). A held statement posts nothing.</summary>
public sealed record StatementSummary
{
    public required string File { get; init; }
    public string? Last4 { get; init; }
    public SourceKind? Kind { get; init; }
    public string? Layout { get; init; }
    public int Rows { get; init; }
    public ReconcileResult? Reconcile { get; init; }
    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<StatementLine> Lines { get; init; } = [];

    [JsonIgnore]
    public bool IsHeld => HoldReason is not null;
}

/// <summary>FR-1…FR-9 for one job. <see cref="FailReason"/> set → the job fails (G2) and nothing is mapped.</summary>
public sealed record AnalysisResult
{
    public required JobInput Input { get; init; }
    public required string Company { get; init; }
    public required SpecReadResult Spec { get; init; }
    public required SpecGateResult Gate { get; init; }
    public IReadOnlyList<StatementSummary> Statements { get; init; } = [];
    public IReadOnlyList<MappedTxn> Lines { get; init; } = [];
    public string QbXml { get; init; } = "";

    public string? FailReason => Gate.Ok ? null : "requirement check failed (G2): " + string.Join("; ", Gate.Errors);

    public IReadOnlyList<MappedTxn> ToPost => Lines.Where(l => l.Decision == Decision.Post).ToList();
}

/// <summary>A line QuickBooks accepted and G5 verified.</summary>
public sealed record PostedLine(MappedTxn Txn, string TxnId, string? EditSequence);

/// <summary>Outcome of FR-10…FR-12. <see cref="Status"/> is <c>posted</c> or <c>partial</c>.</summary>
public sealed record PostOutcome
{
    public required JobStatus Status { get; init; }
    public string? Error { get; init; }

    /// <summary>Null when re-analysis failed before anything was sent.</summary>
    public AnalysisResult? Analysis { get; init; }

    public IReadOnlyList<PostedLine> Posted { get; init; } = [];

    /// <summary>Lines QuickBooks did not accept (or whose outcome is unknown); analysis holds are in <see cref="Analysis"/>.</summary>
    public IReadOnlyList<MappedTxn> Rejected { get; init; } = [];
}
