using System.Globalization;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Extract;

/// <summary>What <see cref="InvoiceExtractor"/> read from one invoice file: facts, or a hold reason with errors.</summary>
public sealed record InvoiceReadResult
{
    /// <summary>File name inside <c>invoices/</c>.</summary>
    public required string File { get; init; }

    public InvoiceFacts? Facts { get; init; }
    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsHeld => HoldReason is not null;
}

/// <summary>
/// Invoice file → text → Hermes T3 → <see cref="InvoiceFacts"/> (spec FR-5, §9.3). PDFs go through <see cref="PdfText"/>
/// (OCR for scanned pages); PNG/JPG images go to <see cref="IOcr"/> when enabled, else the file is held
/// <c>unreadable</c>. Any Hermes failure holds the invoice; invoices are evidence only and never block a job.
/// </summary>
public sealed class InvoiceExtractor(IOcr ocr, IHermesClient hermes, PromptLibrary prompts)
{
    /// <summary>
    /// SPEC-GAP T-401: an invoice is sent in one request (no chunking); text over the T2 chunk size is held instead,
    /// because one part of an invoice may miss its total.
    /// </summary>
    public const int TextLimit = StatementLlmExtractor.ChunkLimit;

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    public async Task<InvoiceReadResult> ReadAsync(JobFile file, string? company, string? auditDir, CancellationToken ct)
    {
        var (text, held) = await ReadTextAsync(file, ct);
        if (held is not null)
        {
            return held;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Held(file.FileName, HoldReasons.Unreadable, "no text found in the file");
        }

        if (text.Length > TextLimit)
        {
            return Held(
                file.FileName,
                HoldReasons.Unreadable,
                string.Create(CultureInfo.InvariantCulture, $"text has {text.Length} characters; invoices over {TextLimit} are not sent to Hermes"));
        }

        var header = string.IsNullOrWhiteSpace(company)
            ? $"Invoice file: {file.FileName}"
            : $"Invoice file: {file.FileName}\nOur company: {company}";
        var request = new HermesRequest(HermesTask.Invoice, prompts.Render(HermesTask.Invoice, NoValues), $"{header}\n\n{text}", auditDir);
        try
        {
            var answer = await hermes.CompleteJsonAsync<InvoiceAnswer>(request, ct);
            return new InvoiceReadResult { File = file.FileName, Facts = answer.ToFacts(file.FileName) };
        }
        catch (HermesException ex)
        {
            return Held(file.FileName, HoldReasons.HermesFailed, ex.Message);
        }
    }

    private async Task<(string? Text, InvoiceReadResult? Held)> ReadTextAsync(JobFile file, CancellationToken ct)
    {
        switch (Path.GetExtension(file.FileName).ToLowerInvariant())
        {
            case ".pdf":
                var pdf = await new PdfText(ocr).ReadAsync(file.Path, ct);
                if (!pdf.IsHeld)
                {
                    return (pdf.Text, null);
                }

                // PdfText names its "cannot read" code after statements; for an invoice it is plain "unreadable".
                var reason = pdf.HoldReason == HoldReasons.UnreadableStatement ? HoldReasons.Unreadable : pdf.HoldReason!;
                return (null, Held(file.FileName, reason, [.. pdf.Errors]));
            case ".png":
            case ".jpg":
                if (!ocr.Enabled)
                {
                    return (null, Held(file.FileName, HoldReasons.Unreadable, "image invoice and OCR is disabled (Ocr.Enabled = false)"));
                }

                try
                {
                    var bytes = await File.ReadAllBytesAsync(file.Path, ct);
                    return (await ocr.ReadImageAsync(bytes, ct), null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    return (null, Held(file.FileName, HoldReasons.Unreadable, $"OCR failed: {ex.GetType().Name}: {ex.Message}"));
                }

            default:
                // FolderReader admits only pdf/png/jpg; anything else is held rather than guessed at.
                return (null, Held(file.FileName, HoldReasons.UnsupportedExtension, $"no invoice reader for {Path.GetExtension(file.FileName)}"));
        }
    }

    private static InvoiceReadResult Held(string fileName, string reason, params string[] errors) => new()
    {
        File = fileName,
        HoldReason = reason,
        Errors = errors,
    };
}
