using QbAutopost.Core.Extract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace QbAutopost.Core.Extraction;

public enum UploadPdfOutcome { Ok, NotAPdf, Protected, TooManyPages, NoText }

/// <summary>
/// What <see cref="UploadPdfReader"/> found. <see cref="Text"/> is empty unless the outcome is <see cref="UploadPdfOutcome.Ok"/>.
/// <see cref="UnreadPages"/> holds the 1-based pages an Ok read treated as empty; null on any other outcome.
/// </summary>
public sealed record UploadPdfResult(UploadPdfOutcome Outcome, int PageCount, string Text, IReadOnlyList<int>? UnreadPages = null);

/// <summary>
/// Upload PDF → text (spec §6.1, §6.2). Bytes in, nothing written, no OCR: a PDF with no readable text is
/// <see cref="UploadPdfOutcome.NoText"/> (Q-105). The job reader <see cref="PdfText"/> is not touched.
/// </summary>
public static class UploadPdfReader
{
    private static readonly byte[] Prefix = "%PDF-"u8.ToArray();

    public static UploadPdfResult Read(byte[] bytes, int maxPages)
    {
        if (!bytes.AsSpan().StartsWith(Prefix))
        {
            return new(UploadPdfOutcome.NotAPdf, 0, "");
        }

        try
        {
            using var document = PdfDocument.Open(bytes);
            var count = document.NumberOfPages;
            if (count > maxPages)
            {
                return new(UploadPdfOutcome.TooManyPages, count, "");
            }

            var pages = new List<string>(count);
            var unread = new List<int>();
            var anyText = false;
            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                var readable = text.Count(c => !char.IsWhiteSpace(c)) >= PdfText.ScannedPageThreshold;
                anyText |= readable;
                pages.Add(readable ? text : "");
                if (!readable)
                {
                    unread.Add(page.Number);
                }
            }

            return anyText
                ? new(UploadPdfOutcome.Ok, count, string.Join(PdfTextResult.PageBreak, pages), unread)
                : new(UploadPdfOutcome.NoText, count, "");
        }
        catch (PdfDocumentEncryptedException)
        {
            return new(UploadPdfOutcome.Protected, 0, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new(UploadPdfOutcome.NotAPdf, 0, "");
        }
    }
}
