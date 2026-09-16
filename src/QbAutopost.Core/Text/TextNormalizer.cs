using System.Text.RegularExpressions;

namespace QbAutopost.Core.Text;

public static partial class TextNormalizer
{
    /// <summary>
    /// Canonical description form used in fingerprints and pattern matching:
    /// trimmed, whitespace runs collapsed to one space, upper-case invariant.
    /// SPEC-GAP T-002: digits and punctuation are kept so distinct transactions stay distinct.
    /// </summary>
    public static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : Whitespace().Replace(text.Trim(), " ").ToUpperInvariant();

    /// <summary>Looser form for fuzzy name matching: <see cref="Normalize"/> with every non-letter/digit run turned into one space.</summary>
    public static string ForMatching(string? text) =>
        NonAlphanumeric().Replace(Normalize(text), " ").Trim();

    /// <summary>True when <paramref name="fragment"/> (normalised) is non-empty and occurs in the already-normalised description.</summary>
    public static bool ContainsFragment(string normalizedDescription, string? fragment)
    {
        var needle = Normalize(fragment);
        return needle.Length > 0 && normalizedDescription.Contains(needle, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumeric();
}
