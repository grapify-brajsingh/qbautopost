using QbAutopost.Core.QbXml;

namespace QbAutopost.Core.Pipeline;

/// <summary>The settings the pipeline needs, with every path already absolute.</summary>
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
}
