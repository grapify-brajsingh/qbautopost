using System.Text;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Extraction;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

public class UploadPdfReaderTests
{
    private const string Long = "Statement line one two three four five six seven eight nine ten";

    private static byte[] Build(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(595, 842);
            page.AddText(text, 12, new PdfPoint(40, 780), font);
        }

        return builder.Build();
    }

    [Fact]
    public void Should_ReturnNotAPdf_When_PrefixMissing()
    {
        var result = UploadPdfReader.Read(Encoding.ASCII.GetBytes("hello world"), 10);
        Assert.Equal(UploadPdfOutcome.NotAPdf, result.Outcome);
    }

    [Fact]
    public void Should_ReturnNotAPdf_When_PdfPigCannotOpen()
    {
        var result = UploadPdfReader.Read(Encoding.ASCII.GetBytes("%PDF-1.4 garbage garbage garbage"), 10);
        Assert.Equal(UploadPdfOutcome.NotAPdf, result.Outcome);
    }

    [Fact]
    public void Should_ReturnProtected_When_Encrypted()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "extractions", "encrypted.pdf"));
        var result = UploadPdfReader.Read(bytes, 10);
        Assert.Equal(UploadPdfOutcome.Protected, result.Outcome);
    }

    [Fact]
    public void Should_ReturnTooManyPages_When_OverLimit()
    {
        var result = UploadPdfReader.Read(Build(Long, Long, Long), 2);
        Assert.Equal(UploadPdfOutcome.TooManyPages, result.Outcome);
        Assert.Equal(3, result.PageCount);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void Should_ReturnNoText_When_EveryPageShort()
    {
        var result = UploadPdfReader.Read(Build("short", "tiny"), 10);
        Assert.Equal(UploadPdfOutcome.NoText, result.Outcome);
        Assert.Equal(2, result.PageCount);
    }

    [Fact]
    public void Should_ReadText_When_OnlySomePagesAreShort()
    {
        var result = UploadPdfReader.Read(Build("short", Long), 10);
        Assert.Equal(UploadPdfOutcome.Ok, result.Outcome);
        Assert.Contains("Statement line one", result.Text);
    }

    [Fact]
    public void Should_JoinPagesWithPageBreak_When_Ok()
    {
        var result = UploadPdfReader.Read(Build(Long, Long.Replace("one", "ONE")), 10);
        Assert.Equal(UploadPdfOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.PageCount);
        var parts = result.Text.Split(PdfTextResult.PageBreak);
        Assert.Equal(2, parts.Length);
        Assert.Contains("ONE", parts[1]);
    }

    [Fact]
    public void Should_ReadRealStatement_When_FixtureUploaded()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "statements", "chase-checking-4521.pdf"));
        Assert.Equal(UploadPdfOutcome.Ok, UploadPdfReader.Read(bytes, 10).Outcome);
    }

    [Fact]
    public void Should_LeaveJobReaderUnchanged_When_UploadReaderAdded()
    {
        Assert.Equal(40, PdfText.ScannedPageThreshold);
        Assert.Equal("\n\f\n", PdfTextResult.PageBreak);
    }
}
