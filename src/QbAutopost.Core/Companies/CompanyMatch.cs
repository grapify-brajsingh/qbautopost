namespace QbAutopost.Core.Companies;

public enum CompanyMatchKind { Matched, None, Ambiguous }

/// <param name="Matchable">False for missing and disabled companies (they never match); true for active and ambiguous.</param>
public sealed record CompanyNames(int CompanyId, bool Matchable, IReadOnlyList<string?> Names);

/// <summary>
/// Result of matching the printed business name to a company. Plain data (no behaviour beyond a factory), so API views
/// serialise and reuse it directly (ruling B17).
/// </summary>
/// <param name="Candidates">Matched: [the id]; Ambiguous: every matching id, ascending; None: [].</param>
public sealed record CompanyMatch(
    CompanyMatchKind Match,
    string? PrintedName,
    string? DbaName,
    int? CompanyId,
    IReadOnlyList<int> Candidates)
{
    public static CompanyMatch NoneFor(string? printedName, string? dbaName) =>
        new(CompanyMatchKind.None, printedName, dbaName, null, []);
}
