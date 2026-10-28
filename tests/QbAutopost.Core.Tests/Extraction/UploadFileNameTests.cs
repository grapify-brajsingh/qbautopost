using QbAutopost.Core.Extraction;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

public class UploadFileNameTests
{
    [Fact]
    public void Should_StripPathAndControlChars_When_Sanitized()
    {
        Assert.Equal("ab.pdf", UploadFileName.Sanitize("..\\a\u0007b.pdf"));
    }

    [Fact]
    public void Should_StripForwardSlashPath_When_Sanitized()
    {
        Assert.Equal("statement.pdf", UploadFileName.Sanitize("C:/x/y/statement.pdf"));
    }

    [Fact]
    public void Should_RemoveC1ControlChars_When_Sanitized()
    {
        Assert.Equal("ab.pdf", UploadFileName.Sanitize("a\u0085b\u007f.pdf"));
    }

    [Fact]
    public void Should_TruncateWithoutSplittingSurrogate_When_Over255()
    {
        var raw = new string('a', 254) + "\U0001F600" + "tail";

        var name = UploadFileName.Sanitize(raw);

        Assert.Equal(new string('a', 254), name);
    }

    [Fact]
    public void Should_CutTo255_When_LongAscii()
    {
        Assert.Equal(255, UploadFileName.Sanitize(new string('b', 400)).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dir/")]
    [InlineData("\u0001\u0002")]
    public void Should_DefaultToUploadPdf_When_NothingLeft(string? raw)
    {
        Assert.Equal("upload.pdf", UploadFileName.Sanitize(raw));
    }
}
