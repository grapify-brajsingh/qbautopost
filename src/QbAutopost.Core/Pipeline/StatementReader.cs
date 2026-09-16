using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Pipeline;

/// <summary>
/// One statement file → rows (spec FR-3): CSV and XLSX by code with the <c>rules.json</c> layouts (never Hermes),
/// PDF → <see cref="PdfText"/> (OCR for scanned pages) → Hermes T2. G1 is applied by the caller.
/// </summary>
public sealed class StatementReader(IOcr ocr, StatementLlmExtractor t2)
{
    public async Task<StatementParseResult> ReadAsync(JobFile file, Rules rules, string hermesAuditDir, CancellationToken ct)
    {
        switch (Path.GetExtension(file.FileName).ToLowerInvariant())
        {
            case ".csv":
                return new CsvStatementParser(rules.CsvLayouts).Parse(file.Path);
            case ".xlsx":
                return new XlsxStatementParser(rules.CsvLayouts).Parse(file.Path);
            case ".pdf":
                var text = await new PdfText(ocr).ReadAsync(file.Path, ct);
                return text.IsHeld
                    ? new StatementParseResult { File = file.FileName, HoldReason = text.HoldReason, Errors = text.Errors }
                    : await t2.ExtractAsync(text, hermesAuditDir, ct);
            default:
                // FolderReader admits only the extensions above; anything else is held rather than guessed at.
                return new StatementParseResult
                {
                    File = file.FileName,
                    HoldReason = HoldReasons.UnsupportedExtension,
                    Errors = [$"no statement reader for {Path.GetExtension(file.FileName)}"],
                };
        }
    }
}
