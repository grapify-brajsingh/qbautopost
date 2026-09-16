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
    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsHeld => HoldReason is not null;
}
