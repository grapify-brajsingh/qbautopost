using QbAutopost.Core.Text;

namespace QbAutopost.Core.Models;

/// <summary>One normalised transaction row from a bank or card statement (spec §7).</summary>
public sealed record StatementLine
{
    public required string SourceFile { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Last4 { get; init; }

    /// <summary>1-based data-row number within the source file (header excluded).</summary>
    public required int LineNo { get; init; }

    public required DateOnly Date { get; init; }
    public required string Description { get; init; }
    public required Direction Direction { get; init; }

    /// <summary>Always positive; <see cref="Direction"/> carries the sign.</summary>
    public required decimal Amount { get; init; }

    public string? CheckNo { get; init; }
    public decimal? Balance { get; init; }

    /// <summary>Computed on every access so a <c>with</c> copy can never carry a stale value.</summary>
    public string Fingerprint => Fingerprints.Compute(this);

    public string RequestId => Fingerprints.ToRequestId(Fingerprint);
}
