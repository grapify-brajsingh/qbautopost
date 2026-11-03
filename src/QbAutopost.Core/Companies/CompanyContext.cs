namespace QbAutopost.Core.Companies;

/// <summary>What a post or sync needs to know about its company: the file to open and the files it keeps.</summary>
public sealed record CompanyContext(int Id, string DisplayName, string FilePath, string RulesFile, string LedgerFile)
{
    public bool IsDefault { get; init; }
    public CompanyState State { get; init; } = CompanyState.Active;
    public CompanyAccess Access { get; init; } = CompanyAccess.Approved;
    public bool CanOpenSession => State == CompanyState.Active && Access == CompanyAccess.Approved;
}
