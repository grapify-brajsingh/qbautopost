using QbAutopost.Core.QbXml;

namespace QbAutopost.Core.Pipeline;

/// <summary>
/// The settings the pipeline needs, with every path already absolute. The company name, rules and ledger here are the
/// default company's files (v1 routes and folder jobs); v2 work uses the <c>CompanyContext</c> of the statement or company
/// it acts on (M17 §4.7).
/// </summary>
public sealed record PipelineOptions
{
    /// <summary><c>Settings.Company.Name</c>: the company whose file the app posts to.</summary>
    public required string CompanyName { get; init; }

    public required string RulesFile { get; init; }
    public required string LedgerFile { get; init; }
    public required string QbListsFile { get; init; }
    public string QbXmlVersion { get; init; } = QbXmlBuilder.DefaultVersion;

    /// <summary>FR-8 <c>QuickBooks:DuplicateWindowDays</c> (W).</summary>
    public int DuplicateWindowDays { get; init; } = 3;

    /// <summary>FR-11 <c>QuickBooks:BackupFolder</c>; blank = no backup check.</summary>
    public string BackupFolder { get; init; } = "";

    public int BackupMaxAgeHours { get; init; } = 36;
}
