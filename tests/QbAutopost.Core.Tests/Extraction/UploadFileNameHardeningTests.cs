using QbAutopost.Core.Extraction;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

/// <summary>T-1212b (Ruling R11): the stored file name loses unpaired surrogates and invisible format/separator chars.</summary>
public class UploadFileNameHardeningTests
{
    [Fact]
    public void Should_DropUnpairedSurrogate_When_Sanitized()
    {
        // A lone high surrogate, a lone low surrogate, and one valid pair that must survive.
        var raw = "a\uD800b\uDC00c\U0001F600.pdf";

        Assert.Equal("abc\U0001F600.pdf", UploadFileName.Sanitize(raw));
    }

    [Fact]
    public void Should_DropBidiAndFormatChars_When_Sanitized()
    {
        // U+202E right-to-left override (Cf), U+200B zero width space (Cf), U+2028 line separator (Zl),
        // U+2029 paragraph separator (Zp), U+FEFF byte order mark (Cf).
        var raw = "\uFEFFinvoice\u202Efdp.exe\u200B" + "\u2028" + "\u2029" + ".pdf";

        Assert.Equal("invoicefdp.exe.pdf", UploadFileName.Sanitize(raw));
    }

    [Fact]
    public void Should_FallBack_When_OnlyFormatCharsRemain()
    {
        Assert.Equal(UploadFileName.Fallback, UploadFileName.Sanitize("\u202E\u200B\uD800"));
    }
}
