using System.Text.Json;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Output;

/// <summary>Writes the files under a job's <c>output/</c> (spec §5, §10). Every write is atomic.</summary>
public static class JobOutputWriter
{
    public const string SpecFile = "spec.json";
    public const string AnalysisFile = "analysis.json";
    public const string ResultFile = "result.json";
    public const string RequestFile = "request.qbxml";
    public const string ResponseFile = "response.qbxml";
    public const string StatementsDir = "statements";

    public static void WriteSpec(string outputDir, AnalysisResult analysis) =>
        AtomicFile.WriteJson(
            Path.Combine(outputDir, SpecFile),
            new SpecDocument(analysis.Spec.Source, analysis.Company, analysis.Spec.Spec, analysis.Gate));

    public static void WriteRows(string outputDir, StatementSummary statement) =>
        AtomicFile.WriteJson(RowsPath(outputDir, statement.File), RowsDocument.From(statement));

    public static string RowsPath(string outputDir, string statementFile) =>
        Path.Combine(outputDir, StatementsDir, statementFile + ".rows.json");

    public static void WriteAnalysis(string outputDir, AnalysisResult analysis) =>
        AtomicFile.WriteJson(
            Path.Combine(outputDir, AnalysisFile),
            new AnalysisDocument(
                analysis.Input.JobId,
                analysis.Company,
                analysis.Spec.Spec,
                analysis.Lines.Select(AnalysisLine.From).ToList()));

    /// <summary>request.qbxml and the three paste-ready sheets, written even in dry run (spec FR-9).</summary>
    public static void WriteRequestAndSheets(string outputDir, AnalysisResult analysis)
    {
        AtomicFile.WriteAllText(Path.Combine(outputDir, RequestFile), analysis.QbXml);
        BatchEnterSheet.WriteAll(outputDir, analysis.ToPost);
    }

    public static void WriteResponse(string outputDir, string qbxml) =>
        AtomicFile.WriteAllText(Path.Combine(outputDir, ResponseFile), qbxml);

    public static void WriteResult(string outputDir, ResultDocument result) =>
        AtomicFile.WriteJson(Path.Combine(outputDir, ResultFile), result);

    /// <summary>The last written result, or null when none exists or it cannot be read.</summary>
    public static ResultDocument? ReadResult(string outputDir)
    {
        var path = Path.Combine(outputDir, ResultFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ResultDocument>(AtomicFile.ReadAllText(path), JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable or still locked after retries: the view falls back to the status record alone.
            return null;
        }
    }
}
