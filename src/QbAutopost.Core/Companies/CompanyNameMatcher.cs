namespace QbAutopost.Core.Companies;

/// <summary>
/// M17 spec §4.5: matches the business name printed on a statement to a company by exact equality after
/// normalising. No fuzzy distance; the model only reads the name, this code decides.
/// </summary>
public static class CompanyNameMatcher
{
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
        { "LLC", "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "LTD", "LIMITED", "THE", "DBA" };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var chars = name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray(); // '&' and all else -> space
        var tokens = new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 0; i + 2 < tokens.Count; i++)                       // the sequence L L C (from "L.L.C."), Q-149
            if (tokens[i] == "L" && tokens[i + 1] == "L" && tokens[i + 2] == "C") { tokens.RemoveRange(i, 3); i--; }
        return string.Concat(tokens.Where(t => !Dropped.Contains(t)));
    }

    public static CompanyMatch Match(string? printedName, string? dbaName, IEnumerable<CompanyNames> companies)
    {
        var keys = new[] { Normalize(printedName), Normalize(dbaName) }
            .Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return CompanyMatch.NoneFor(printedName, dbaName);

        var ids = companies
            .Where(c => c.Matchable && c.Names.Any(n => Normalize(n) is { Length: > 0 } k && keys.Contains(k)))
            .Select(c => c.CompanyId).Distinct().Order().ToList();

        return ids.Count switch
        {
            0 => CompanyMatch.NoneFor(printedName, dbaName),
            1 => new CompanyMatch(CompanyMatchKind.Matched, printedName, dbaName, ids[0], ids),
            _ => new CompanyMatch(CompanyMatchKind.Ambiguous, printedName, dbaName, null, ids),
        };
    }
}
