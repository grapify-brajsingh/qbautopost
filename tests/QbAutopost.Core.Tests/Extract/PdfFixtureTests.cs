using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

/// <summary>
/// The committed PDF fixtures (<c>tests/fixtures/statements/*.pdf</c>, rendered from the matching <c>.pdf.txt</c>)
/// read back through <see cref="PdfText"/> with every text line intact, so T2 tests start from real extraction.
/// </summary>
public sealed class PdfFixtureTests
{
    [Theory]
    [InlineData("chase-checking-4521", 2)]
    [InlineData("chase-card-7788", 1)]
    public async Task Should_ExtractEveryFixtureLine_When_CommittedPdfIsRead(string name, int pages)
    {
        var expected = Fixtures.Read("statements", name + ".pdf.txt")
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line => line.Length > 0 && line != "<<PAGE>>")
            .Select(Squash)
            .ToList();

        var result = await new PdfText(new DisabledOcr()).ReadAsync(Fixtures.PathOf("statements", name + ".pdf"), CancellationToken.None);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(pages, result.Pages.Count);
        Assert.All(result.Pages, p => Assert.False(p.Scanned));
        var text = Squash(result.Text);
        Assert.All(expected, line => Assert.Contains(line, text, StringComparison.Ordinal));
    }

    /// <summary>Whitespace-insensitive comparison: PdfPig may split or join spaces differently from the source text.</summary>
    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
