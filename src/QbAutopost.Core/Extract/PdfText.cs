using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace QbAutopost.Core.Extract;

/// <summary>Text of one PDF page. <see cref="OcrImages"/> counts the images whose OCR text was appended.</summary>
public sealed record PdfPageText(int Number, string Text, bool Scanned, int OcrImages);

/// <summary>What <see cref="PdfText"/> read from one file. A held result must not be sent to Hermes.</summary>
public sealed record PdfTextResult
{
    public const string PageBreak = "\n\f\n";

    public required string File { get; init; }
    public IReadOnlyList<PdfPageText> Pages { get; init; } = [];
    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsHeld => HoldReason is not null;

    /// <summary>All pages in order, separated by <see cref="PageBreak"/> (a form feed on its own line).</summary>
    public string Text => string.Join(PageBreak, Pages.Select(p => p.Text));
}

/// <summary>
/// PDF → text per page with PdfPig (spec FR-3). A page with fewer than <see cref="ScannedPageThreshold"/> characters
/// is scanned: its images go to <see cref="IOcr"/> when OCR is enabled, otherwise the whole file is held
/// <c>scanned-pdf-ocr-disabled</c>. Files PdfPig cannot open, and OCR failures, hold the file <c>unreadable-statement</c>.
/// </summary>
public sealed class PdfText(IOcr ocr)
{
    /// <summary>SPEC-GAP T-302: "characters" counted without whitespace, so a page of spaces and line breaks is scanned.</summary>
    public const int ScannedPageThreshold = 40;

    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    public async Task<PdfTextResult> ReadAsync(string path, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        List<RawPage> raw;
        try
        {
            raw = ReadPages(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // PdfPig throws several exception types for damaged, encrypted or non-PDF files.
            return Held(fileName, [], $"pdf could not be read: {ex.GetType().Name}: {ex.Message}");
        }

        if (raw.Count == 0)
        {
            return Held(fileName, [], "pdf has no pages");
        }

        var scanned = raw.Where(p => p.Scanned).Select(p => p.Number).ToList();
        if (scanned.Count > 0 && !ocr.Enabled)
        {
            return new PdfTextResult
            {
                File = fileName,
                Pages = raw.Select(p => new PdfPageText(p.Number, p.Text, p.Scanned, 0)).ToList(),
                HoldReason = HoldReasons.ScannedPdfOcrDisabled,
                Errors = [$"page {string.Join(", ", scanned)} has fewer than {ScannedPageThreshold} characters of text and OCR is disabled"],
            };
        }

        var pages = new List<PdfPageText>();
        foreach (var page in raw)
        {
            if (!page.Scanned)
            {
                pages.Add(new PdfPageText(page.Number, page.Text, false, 0));
                continue;
            }

            if (page.UndecodableImages > 0)
            {
                return Held(fileName, pages, $"page {page.Number}: {page.UndecodableImages} image(s) in a format OCR cannot read");
            }

            // SPEC-GAP T-302: a scanned page without images has nothing to OCR; its own (short) text is kept and G1 checks the rows.
            var parts = new List<string> { page.Text };
            foreach (var image in page.Images)
            {
                try
                {
                    parts.Add(await ocr.ReadImageAsync(image, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    return Held(fileName, pages, $"page {page.Number}: OCR failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var text = string.Join('\n', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
            pages.Add(new PdfPageText(page.Number, text, true, page.Images.Count));
        }

        return new PdfTextResult { File = fileName, Pages = pages };
    }

    private static List<RawPage> ReadPages(string path)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<RawPage>();
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            var scanned = text.Count(c => !char.IsWhiteSpace(c)) < ScannedPageThreshold;
            var images = new List<byte[]>();
            var undecodable = 0;
            if (scanned)
            {
                foreach (var image in page.GetImages())
                {
                    if (Encode(image) is { } bytes)
                    {
                        images.Add(bytes);
                    }
                    else
                    {
                        undecodable++;
                    }
                }
            }

            pages.Add(new RawPage(page.Number, text, scanned, images, undecodable));
        }

        return pages;
    }

    /// <summary>JPEG streams are passed through as they are; anything PdfPig can decode is re-encoded as PNG.</summary>
    private static byte[]? Encode(IPdfImage image)
    {
        var raw = image.RawMemory.Span;
        if (raw.StartsWith(JpegMagic))
        {
            return raw.ToArray();
        }

        return image.TryGetPng(out var png) ? png : null;
    }

    private static PdfTextResult Held(string fileName, IReadOnlyList<PdfPageText> pages, string error) => new()
    {
        File = fileName,
        Pages = pages,
        HoldReason = HoldReasons.UnreadableStatement,
        Errors = [error],
    };

    private sealed record RawPage(int Number, string Text, bool Scanned, IReadOnlyList<byte[]> Images, int UndecodableImages);
}
