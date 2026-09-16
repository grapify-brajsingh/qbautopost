using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>One page for <see cref="PdfBuilder"/>: text lines (Helvetica, top down) and images (PNG or JPEG bytes).</summary>
internal sealed record PdfPageSpec(IReadOnlyList<string> Lines, IReadOnlyList<TestImage> Images)
{
    public static PdfPageSpec Text(params string[] lines) => new(lines, []);

    public static PdfPageSpec Scan(params TestImage[] images) => new([], images);
}

/// <summary>Writes small PDFs with PdfPig's builder for <c>PdfText</c> tests.</summary>
internal static class PdfBuilder
{
    public static void Write(string path, params PdfPageSpec[] pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var spec in pages)
        {
            var page = builder.AddPage(612, 792);
            var y = 740d;
            foreach (var line in spec.Lines)
            {
                page.AddText(line, 10, new PdfPoint(40, y), font);
                y -= 14;
            }

            var top = 700d;
            foreach (var image in spec.Images)
            {
                var box = new PdfRectangle(40, top - 100, 240, top);
                _ = image.IsPng ? page.AddPng(image.Bytes, box) : page.AddJpeg(image.Bytes, box);
                top -= 120;
            }
        }

        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>A text line of exactly <paramref name="letters"/> non-space characters (spaces added between groups of ten).</summary>
    public static string Letters(int letters) =>
        string.Join(' ', Enumerable.Range(0, letters).Select(i => (char)('A' + (i % 26))).Chunk(10).Select(c => new string(c)));
}
