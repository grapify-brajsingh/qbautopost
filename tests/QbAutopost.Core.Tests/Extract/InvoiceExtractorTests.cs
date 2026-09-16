using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

/// <summary>Hermes T3 (spec FR-5, §9.3): invoice file → text (PdfText, or OCR for images) → <see cref="InvoiceFacts"/>.</summary>
public sealed class InvoiceExtractorTests : IDisposable
{
    private const string AuditDir = "audit-dir";
    private const string Company = "Tropicana Properties LLC";

    private static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Invoice);

    private readonly TempJobFolder _temp = new();
    private readonly ScriptedHermes _hermes = new(_ => Fixtures.Read("hermes", "invoice.json"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Should_ReturnFacts_When_TextPdfIsRead()
    {
        var result = await Read(new DisabledOcr(), FixturePdf());

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("home-depot-88213.pdf", result.File);
        Assert.Equal(
            new InvoiceFacts
            {
                File = "home-depot-88213.pdf",
                Party = "Home Depot",
                Role = PartyRole.Vendor,
                Number = "88213",
                Date = new DateOnly(2026, 8, 21),
                Total = 184.32m,
                CategoryHint = "plumbing fittings, repair",
            },
            result.Facts);
    }

    [Fact]
    public async Task Should_SendInvoiceTaskWithFileNameAndPdfText_When_TextPdfIsRead()
    {
        await Read(new DisabledOcr(), FixturePdf());

        var request = Assert.Single(_hermes.Requests);
        Assert.Equal(HermesTask.Invoice, request.Task);
        Assert.Equal(AuditDir, request.AuditDir);
        Assert.DoesNotContain("{{", request.SystemPrompt, StringComparison.Ordinal);
        Assert.StartsWith("Invoice file: home-depot-88213.pdf\nOur company: Tropicana Properties LLC\n\n", request.UserContent, StringComparison.Ordinal);
        Assert.Contains("TOTAL 184.32", request.UserContent, StringComparison.Ordinal);
        Assert.Contains("Invoice #: 88213", request.UserContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Should_OmitCompanyLine_When_CompanyIsUnknown(string? company)
    {
        await Read(new DisabledOcr(), FixturePdf(), company: company);

        var content = Assert.Single(_hermes.Requests).UserContent;
        Assert.StartsWith("Invoice file: home-depot-88213.pdf\n\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Our company", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("receipt.png")]
    [InlineData("receipt.jpg")]
    [InlineData("RECEIPT.JPG")]
    public async Task Should_OcrTheImageAndSendItsText_When_InvoiceIsAnImageAndOcrIsEnabled(string name)
    {
        var bytes = TestImage.Png().Bytes;
        var ocr = new FakeOcr().Respond(_ => "THE HOME DEPOT\nTOTAL 184.32");

        var result = await Read(ocr, WriteBytes(name, bytes));

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(184.32m, result.Facts!.Total);
        Assert.Equal(name, result.Facts.File);
        Assert.Equal(bytes, Assert.Single(ocr.Images));
        Assert.EndsWith("THE HOME DEPOT\nTOTAL 184.32", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("receipt.png")]
    [InlineData("receipt.jpg")]
    public async Task Should_HoldUnreadable_When_InvoiceIsAnImageAndOcrIsDisabled(string name)
    {
        var result = await Read(new FakeOcr(enabled: false), WriteBytes(name, TestImage.Png().Bytes));

        Assert.Equal(HoldReasons.Unreadable, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("OCR is disabled", StringComparison.Ordinal));
        Assert.Null(result.Facts);
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_HoldUnreadable_When_OcrFails()
    {
        var ocr = new FakeOcr().Respond(_ => throw new InvalidOperationException("engine crashed"));

        var result = await Read(ocr, WriteBytes("receipt.png", TestImage.Png().Bytes));

        Assert.Equal(HoldReasons.Unreadable, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("engine crashed", StringComparison.Ordinal));
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_PropagateCancellation_When_OcrIsCancelled()
    {
        var ocr = new FakeOcr().Respond(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(ocr, WriteBytes("receipt.png", TestImage.Png().Bytes)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t ")]
    public async Task Should_HoldUnreadable_When_OcrFindsNoText(string text)
    {
        var result = await Read(new FakeOcr().Respond(_ => text), WriteBytes("receipt.png", TestImage.Png().Bytes));

        Assert.Equal(HoldReasons.Unreadable, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("no text", StringComparison.Ordinal));
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_HoldScannedPdf_When_OcrIsDisabled()
    {
        var path = WritePdf("scan.pdf", PdfPageSpec.Scan(TestImage.Png()));

        var result = await Read(new DisabledOcr(), path);

        Assert.Equal(HoldReasons.ScannedPdfOcrDisabled, result.HoldReason);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_SendOcrText_When_PdfIsScannedAndOcrIsEnabled()
    {
        var path = WritePdf("scan.pdf", PdfPageSpec.Scan(TestImage.Png()));
        var ocr = new FakeOcr().Respond(_ => "HOME DEPOT INVOICE 88213 TOTAL 184.32");

        var result = await Read(ocr, path);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Single(ocr.Images);
        Assert.Contains("HOME DEPOT INVOICE 88213 TOTAL 184.32", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_HoldUnreadable_When_PdfIsCorrupt()
    {
        var result = await Read(new DisabledOcr(), WriteBytes("broken.pdf", "not a pdf"u8.ToArray()));

        Assert.Equal(HoldReasons.Unreadable, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("pdf could not be read", StringComparison.Ordinal));
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_HoldUnreadable_When_TextIsOverTheLimit()
    {
        var ocr = new FakeOcr().Respond(_ => new string('A', InvoiceExtractor.TextLimit + 1));

        var result = await Read(ocr, WriteBytes("huge.png", TestImage.Png().Bytes));

        Assert.Equal(HoldReasons.Unreadable, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("60000", StringComparison.Ordinal));
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_SendText_When_ItIsExactlyAtTheLimit()
    {
        var ocr = new FakeOcr().Respond(_ => new string('A', InvoiceExtractor.TextLimit));

        var result = await Read(ocr, WriteBytes("big.png", TestImage.Png().Bytes));

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Single(_hermes.Requests);
    }

    [Fact]
    public async Task Should_HoldHermesFailed_When_AnswerIsInvalidTwice()
    {
        var hermes = new ScriptedHermes(_ => """{ "party": "Home Depot", "role": "supplier", "total": 0 }""");

        var result = await Read(new DisabledOcr(), FixturePdf(), hermes);

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("role", StringComparison.Ordinal));
        Assert.Null(result.Facts);
    }

    [Fact]
    public async Task Should_ReturnFacts_When_RealClientSucceedsOnRetry()
    {
        using var handler = new StubHttpHandler()
            .ReplyContent("""{ "party": "Home Depot", "role": "store", "date": "08/21/2026", "total": 184.32 }""")
            .ReplyContent("```json\n" + Fixtures.Read("hermes", "invoice.json") + "\n```");
        using var http = new HttpClient(handler);
        var client = new HermesClient(http, new HermesOptions { BaseUrl = "http://hermes.test" });

        var result = await new InvoiceExtractor(new DisabledOcr(), client, Prompts)
            .ReadAsync(new JobFile(FixturePdf(), "home-depot-88213.pdf", null), Company, null, CancellationToken.None);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(184.32m, result.Facts!.Total);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("role", handler.Requests[1].Message(1), StringComparison.Ordinal);
        Assert.Contains("date", handler.Requests[1].Message(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_HoldHermesFailed_When_HermesIsUnreachable()
    {
        var hermes = new ScriptedHermes(_ => throw new HermesUnavailableException(HermesTask.Invoice, "connection refused"));

        var result = await Read(new DisabledOcr(), FixturePdf(), hermes);

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("connection refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HoldUnsupportedExtension_When_FileIsNotPdfOrImage()
    {
        var result = await Read(new FakeOcr(), WriteBytes("invoice.txt", "TOTAL 184.32"u8.ToArray()));

        Assert.Equal(HoldReasons.UnsupportedExtension, result.HoldReason);
        Assert.Empty(_hermes.Requests);
    }

    private Task<InvoiceReadResult> Read(IOcr ocr, string path, ScriptedHermes? hermes = null, string? company = Company) =>
        new InvoiceExtractor(ocr, hermes ?? _hermes, Prompts).ReadAsync(
            new JobFile(path, Path.GetFileName(path), null), company, AuditDir, CancellationToken.None);

    private static string FixturePdf() => Fixtures.PathOf("invoices", "home-depot-88213.pdf");

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = _temp.PathOf("invoices", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WritePdf(string name, params PdfPageSpec[] pages)
    {
        var path = _temp.PathOf("invoices", name);
        PdfBuilder.Write(path, pages);
        return path;
    }
}
