using QbAutopost.Core.Models;

namespace QbAutopost.Core.Extract;

/// <summary>What a statement parser produced for one file. A held statement posts nothing (spec FR-3/FR-4).</summary>
public sealed record StatementParseResult
{
    public required string File { get; init; }
    public SourceKind? Kind { get; init; }
    public string? Last4 { get; init; }

    /// <summary>Layout name from <c>rules.json.CsvLayouts</c>, or <c>hermes-t2</c> for PDFs.</summary>
    public string? Layout { get; init; }

    public IReadOnlyList<StatementLine> Rows { get; init; } = [];

    /// <summary>Summary figures printed on the statement (T2 only; null for CSV/XLSX). Checked by G1 (spec FR-4).</summary>
    public StatementTotals? Totals { get; init; }

    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsHeld => HoldReason is not null;
}

/// <summary>
/// Statement-level figures read by Hermes T2 (spec §9.2). Each is null when the statement does not print it;
/// the model reports them, code never derives them.
/// </summary>
public sealed record StatementTotals(
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    int? TransactionCount);
