namespace QbAutopost.Core.Models;

/// <summary>A statement line after mapping to QuickBooks objects (spec §7, FR-6).</summary>
public sealed record MappedTxn
{
    public const int MaxMemoLength = 4095;

    public required StatementLine Line { get; init; }
    public required TxnKind Kind { get; init; }
    public required Decision Decision { get; init; }
    public required Confidence Confidence { get; init; }

    /// <summary>Reason code (see <see cref="HoldReasons"/>) when <see cref="Decision"/> is Hold or Skip.</summary>
    public string? Reason { get; init; }

    /// <summary>Header account: the bank or credit-card account in QuickBooks.</summary>
    public string? Account { get; init; }

    public string? Payee { get; init; }
    public string? LineAccount { get; init; }
    public string? RefNumber { get; init; }

    /// <summary>Account tier that decided <see cref="LineAccount"/> (1 rule, 2 history, 3 invoice, 4 model); null when not tiered.</summary>
    public int? Tier { get; init; }

    /// <summary>Hermes T4 confidence (0…1) when tier 3 or 4 was asked; null otherwise.</summary>
    public double? ModelConfidence { get; init; }

    public string? Note { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = [];
    public string? InvoiceRef { get; init; }

    public string RequestId => Line.RequestId;

    public string Memo => Line.Description.Length <= MaxMemoLength
        ? Line.Description
        : Line.Description[..MaxMemoLength];
}
