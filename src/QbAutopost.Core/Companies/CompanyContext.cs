using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Companies;

/// <summary>What a post or sync needs to know about its company: the file to open and the files it keeps.</summary>
public sealed record CompanyContext(int Id, string DisplayName, string FilePath, string RulesFile, string LedgerFile)
{
    public bool IsDefault { get; init; }
    public CompanyState State { get; init; } = CompanyState.Active;
    public CompanyAccess Access { get; init; } = CompanyAccess.Approved;

    /// <summary>
    /// Ruling B14: the file is neither under <c>Companies:RootFolder</c> nor the configured default <c>Company:FilePath</c>,
    /// so this app never opens it, whatever the state and access say.
    /// </summary>
    public bool FileOutsideRoot { get; init; }

    public bool CanOpenSession => State == CompanyState.Active && Access == CompanyAccess.Approved && !FileOutsideRoot;

    /// <summary>M17 §4.7: the gateway target for this company; null for the default company (its calls stay as in v1).</summary>
    public QbTarget? Target => IsDefault ? null : new QbTarget(FilePath);
}
