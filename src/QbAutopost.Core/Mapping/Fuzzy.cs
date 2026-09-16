using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

public readonly record struct FuzzyHit(string Name, double Score);

/// <summary>
/// Name similarity for payee resolution (spec FR-6).
/// SPEC-GAP T-002: normalised Levenshtein similarity, taking the best window of description tokens
/// that has as many tokens as the name (so "HOME DEPOT #4521 NOIDA" scores 1.0 against "Home Depot").
/// </summary>
public static class Fuzzy
{
    public static double Similarity(string a, string b)
    {
        var x = TextNormalizer.ForMatching(a);
        var y = TextNormalizer.ForMatching(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        return x == y ? 1.0 : 1.0 - ((double)Levenshtein(x, y) / Math.Max(x.Length, y.Length));
    }

    public static double MatchScore(string description, string name)
    {
        var nameForm = TextNormalizer.ForMatching(name);
        var tokens = TextNormalizer.ForMatching(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nameForm.Length == 0 || tokens.Length == 0)
        {
            return 0;
        }

        var width = nameForm.Split(' ').Length;
        if (tokens.Length <= width)
        {
            return Similarity(string.Join(' ', tokens), nameForm);
        }

        var best = 0.0;
        for (var i = 0; i + width <= tokens.Length; i++)
        {
            best = Math.Max(best, Similarity(string.Join(' ', tokens, i, width), nameForm));
        }

        return best;
    }

    /// <summary>Best <paramref name="take"/> names for a description, highest score first, ties by name.</summary>
    public static IReadOnlyList<FuzzyHit> Rank(string description, IEnumerable<string> names, int take) =>
        names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new FuzzyHit(n, MatchScore(description, n)))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .Take(take)
            .ToList();

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
