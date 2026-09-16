using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.Api;

/// <summary><c>Ocr.Enabled</c> wiring (spec FR-3, §12). No test loads the real Tesseract engine.</summary>
public sealed class OcrApiTests
{
    [Fact]
    public void Should_UseDisabledOcr_When_OcrIsNotEnabled()
    {
        using var factory = new ApiFactory();

        var ocr = factory.Services.GetRequiredService<IOcr>();

        Assert.IsType<DisabledOcr>(ocr);
        Assert.False(ocr.Enabled);
    }

    [Fact]
    public void Should_RefuseToStart_When_OcrIsEnabledWithoutLanguageData()
    {
        using var factory = new ApiFactory();
        var enabled = WithOcr(factory, factory.Dir.Combine("no-tessdata"));

        var ex = Assert.ThrowsAny<Exception>(() => enabled.CreateClient());

        Assert.Contains("eng.traineddata", ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("Ocr:TessDataPath", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseToStart_When_OcrIsEnabledWithEmptyPath()
    {
        using var factory = new ApiFactory();
        var enabled = WithOcr(factory, "");

        var ex = Assert.ThrowsAny<Exception>(() => enabled.CreateClient());

        Assert.Contains("Ocr:TessDataPath", ex.ToString(), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> WithOcr(ApiFactory factory, string tessDataPath) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:Enabled"] = "true",
                ["Ocr:TessDataPath"] = tessDataPath,
            })));
}
