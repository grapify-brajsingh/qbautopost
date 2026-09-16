using QbAutopost.Core.Models;

namespace QbAutopost.Core.Mapping;

/// <summary>
/// How to read one bank's CSV/XLSX export (<c>rules.json.CsvLayouts</c>, spec FR-3).
/// SPEC-GAP T-002: shape defined here because the POC layout schema was unavailable.
/// A layout applies when every <see cref="HeaderContains"/> entry is a header cell (trimmed, case-insensitive).
/// Amounts come either from one signed <see cref="AmountColumn"/> or from <see cref="DebitColumn"/>/<see cref="CreditColumn"/>.
/// </summary>
public sealed record CsvLayout
{
    public required SourceKind Kind { get; init; }
    public IReadOnlyList<string> HeaderContains { get; init; } = [];
    public required string DateColumn { get; init; }

    /// <summary>Exact <c>DateOnly</c> format (e.g. <c>MM/dd/yyyy</c>). When set, a cell that does not match is a row error — no fallback.</summary>
    public string? DateFormat { get; init; }

    public required string DescriptionColumn { get; init; }
    public string? AmountColumn { get; init; }

    /// <summary>For <see cref="AmountColumn"/>: false (default) → negative = debit; true → positive = debit (e.g. card exports that list charges as positive).</summary>
    public bool PositiveIsDebit { get; init; }

    public string? DebitColumn { get; init; }
    public string? CreditColumn { get; init; }
    public string? CheckNoColumn { get; init; }
    public string? BalanceColumn { get; init; }
    public string? Last4Column { get; init; }

    public bool Matches(IReadOnlyCollection<string> headerCells) =>
        HeaderContains.Count > 0
        && HeaderContains.All(h => headerCells.Contains(h.Trim(), StringComparer.OrdinalIgnoreCase));
}
