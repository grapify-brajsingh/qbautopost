namespace QbAutopost.Core.Models;

/// <summary>The job's requirement, parsed (spec §7, FR-2).</summary>
public sealed record JobSpec
{
    /// <summary>Null when the requirement names no company or only the template placeholder; the caller supplies the configured name.</summary>
    public string? Company { get; init; }

    public IReadOnlyList<TxnKind> Kinds { get; init; } = [];
    public IReadOnlyList<string> BankLast4 { get; init; } = [];
    public IReadOnlyList<string> CardLast4 { get; init; } = [];

    /// <summary>Section (<c>check</c>, <c>card</c>, <c>deposit</c>) → field → rule text, as written in the requirement.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> FieldRules { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();
}
