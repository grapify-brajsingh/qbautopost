using System.Text.Json;

namespace QbAutopost.Core.Companies;

public enum CompanyAccess { Unchecked, Approved, NeedsApproval, FileNotFound, Error }

public enum CompanySource { Matched, Chosen, Migrated, Default }

/// <summary>A row of <c>companies</c> (M17 spec 4.3).</summary>
public sealed record Company(
    int Id,
    string? FolderName,
    string? Code,
    string DisplayName,
    string? FilePath,
    IReadOnlyList<string> CandidateFiles,
    CompanyState State,
    CompanyAccess Access,
    DateTime? AccessCheckedUtc,
    string? AccessMessage,
    string? QbCompanyName,
    string? QbLegalName,
    IReadOnlyList<string> MatchNames,
    bool IsDefault,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    int RowVersion)
{
    /// <summary>Only an active, approved company opens a QuickBooks session (the admin's Check access is the exception).</summary>
    public bool CanOpenSession => State == CompanyState.Active && Access == CompanyAccess.Approved;

    public CompanyNames ForMatching() => new(
        Id,
        State is CompanyState.Active or CompanyState.Ambiguous,
        [DisplayName, QbCompanyName, QbLegalName, .. MatchNames]);
}

/// <summary>The kebab-case wire names (<c>needs-approval</c>, <c>file-not-found</c>, <c>waiting-only</c> ...).</summary>
public static class CompanyText
{
    public static string Of(CompanyState value) => Kebab(value);
    public static string Of(CompanyAccess value) => Kebab(value);
    public static string Of(CompanySource value) => Kebab(value);
    public static string Of(CompanyMatchKind value) => Kebab(value);

    private static string Kebab<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}

public static class CompanyRules
{
    /// <summary>State of a disabled company that is switched back on (ruling B13): its chosen file is there, so active
    /// (<paramref name="fileExists"/> null means "not checked": trust it); no chosen file but candidates, so ambiguous; else missing.</summary>
    public static CompanyState ReEnabledState(string? filePath, int candidateCount, bool? fileExists) =>
        filePath is not null ? (fileExists ?? true ? CompanyState.Active : CompanyState.Missing)
        : candidateCount > 0 ? CompanyState.Ambiguous
        : CompanyState.Missing;
}
