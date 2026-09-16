using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

/// <summary>
/// A description fragment and the account it implies.
/// SPEC-GAP T-002: <see cref="Match"/> is a plain substring of the normalised description, not a regex.
/// </summary>
public sealed record PatternRule
{
    public required string Match { get; init; }
    public required string Account { get; init; }

    public bool IsMatch(string normalizedDescription) =>
        !string.IsNullOrWhiteSpace(Account) && TextNormalizer.ContainsFragment(normalizedDescription, Match);
}
