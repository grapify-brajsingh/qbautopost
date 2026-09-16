using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

public sealed class PdfTextTests : IDisposable
{
    private const string StatementLine1 = "08/05/2026 CHECK 1043 -420.00 11275.87";
    private const string StatementLine2 = "08/12/2026 ACH DEBIT FPL ELECTRIC UTILITY -311.40 13364.47";

    private readonly TempJobFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Should_ReturnTextOfEveryPage_When_PdfHasATextLayer()
    {
        var path = Write(PdfPageSpec.Text("CHASE TOTAL CHECKING ACCOUNT 4521", StatementLine1), PdfPageSpec.Text(StatementLine2, "Page 2 of 2"));
        var ocr = new FakeOcr();

        var result = await Read(path, ocr);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal([1, 2], result.Pages.Select(p => p.Number));
        Assert.All(result.Pages, p => Assert.False(p.Scanned));
        Assert.Contains("CHECK 1043", result.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("FPL ELECTRIC", result.Pages[1].Text, StringComparison.Ordinal);
        Assert.Empty(ocr.Images);
    }

    [Fact]
    public async Task Should_JoinPagesInOrderWithPageBreaks_When_TextIsRequested()
    {
        var path = Write(PdfPageSpec.Text(StatementLine1), PdfPageSpec.Text(StatementLine2));

        var result = await Read(path, new DisabledOcr());

        Assert.Equal($"{result.Pages[0].Text}\n\f\n{result.Pages[1].Text}", result.Text);
        Assert.True(result.Text.IndexOf("CHECK 1043", StringComparison.Ordinal) < result.Text.IndexOf("FPL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HoldAsScanned_When_PageHasFewerThanFortyCharactersAndOcrIsDisabled()
    {
        var path = Write(PdfPageSpec.Text(StatementLine2), PdfPageSpec.Scan(TestImage.Png()));

        var result = await Read(path, new DisabledOcr());

        Assert.Equal(HoldReasons.ScannedPdfOcrDisabled, result.HoldReason);
        Assert.Equal([false, true], result.Pages.Select(p => p.Scanned));
        Assert.Contains(result.Errors, e => e.Contains("page 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_TreatPageAsScanned_When_ItHasThirtyNineNonSpaceCharacters()
    {
        var path = Write(PdfPageSpec.Text(PdfBuilder.Letters(39)));

        var result = await Read(path, new DisabledOcr());

        Assert.True(Assert.Single(result.Pages).Scanned);
        Assert.Equal(HoldReasons.ScannedPdfOcrDisabled, result.HoldReason);
    }

    [Fact]
    public async Task Should_TreatPageAsText_When_ItHasFortyNonSpaceCharacters()
    {
        var path = Write(PdfPageSpec.Text(PdfBuilder.Letters(40)));

        var result = await Read(path, new DisabledOcr());

        Assert.False(Assert.Single(result.Pages).Scanned);
        Assert.False(result.IsHeld);
    }

    [Fact]
    public async Task Should_OcrPngImagesOfScannedPage_When_OcrIsEnabled()
    {
        var path = Write(PdfPageSpec.Text(StatementLine2), PdfPageSpec.Scan(TestImage.Png(), TestImage.Png(8, 2)));
        var ocr = new FakeOcr().Respond(_ => "08/15/2026 ACH DEBIT UNKNOWN PLUMBER SVC -3199.70");

        var result = await Read(path, ocr);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(2, ocr.Images.Count);
        Assert.All(ocr.Images, image => Assert.Equal(TestImage.PngSignature, image.Take(8)));
        var scanned = result.Pages[1];
        Assert.True(scanned.Scanned);
        Assert.Equal(2, scanned.OcrImages);
        Assert.Contains("UNKNOWN PLUMBER", scanned.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_PassJpegBytesUnchanged_When_ScannedPageHoldsAJpeg()
    {
        var jpeg = TestImage.Jpeg();
        var path = Write(PdfPageSpec.Scan(jpeg));
        var ocr = new FakeOcr();

        await Read(path, ocr);

        Assert.Equal(jpeg.Bytes, Assert.Single(ocr.Images));
    }

    [Fact]
    public async Task Should_KeepPageText_When_OcrIsEnabledButScannedPageHasNoImages()
    {
        var path = Write(PdfPageSpec.Text(StatementLine1), PdfPageSpec.Text("Page 2 of 2"));
        var ocr = new FakeOcr();

        var result = await Read(path, ocr);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Empty(ocr.Images);
        Assert.True(result.Pages[1].Scanned);
        Assert.Equal(0, result.Pages[1].OcrImages);
        Assert.Contains("Page 2 of 2", result.Pages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NotOcrImages_When_PageHasEnoughText()
    {
        var path = Write(new PdfPageSpec([StatementLine1, StatementLine2], [TestImage.Png()]));
        var ocr = new FakeOcr();

        var result = await Read(path, ocr);

        Assert.Empty(ocr.Images);
        Assert.Equal(0, Assert.Single(result.Pages).OcrImages);
    }

    [Fact]
    public async Task Should_HoldAsUnreadable_When_OcrFails()
    {
        var path = Write(PdfPageSpec.Scan(TestImage.Png()));
        var ocr = new FakeOcr().Respond(_ => throw new InvalidOperationException("tessdata missing"));

        var result = await Read(path, ocr);

        Assert.Equal(HoldReasons.UnreadableStatement, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("page 1", StringComparison.Ordinal) && e.Contains("tessdata missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_StopWithCancellation_When_OcrIsCancelled()
    {
        var path = Write(PdfPageSpec.Scan(TestImage.Png()));
        var ocr = new FakeOcr().Respond(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(path, ocr));
    }

    [Fact]
    public async Task Should_HoldAsUnreadable_When_FileIsNotAPdf()
    {
        var path = _temp.WithFile(Path.Combine("statements", "chase-4521.pdf"), "not a pdf").PathOf("statements", "chase-4521.pdf");

        var result = await Read(path, new FakeOcr());

        Assert.Equal(HoldReasons.UnreadableStatement, result.HoldReason);
        Assert.Equal("chase-4521.pdf", result.File);
        Assert.Empty(result.Pages);
    }

    private static Task<PdfTextResult> Read(string path, IOcr ocr) => new PdfText(ocr).ReadAsync(path, CancellationToken.None);

    private string Write(params PdfPageSpec[] pages)
    {
        var path = _temp.PathOf("statements", "chase-4521.pdf");
        PdfBuilder.Write(path, pages);
        return path;
    }
}
