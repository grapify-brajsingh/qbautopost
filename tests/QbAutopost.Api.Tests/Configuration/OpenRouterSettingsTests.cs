using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Api.Tests.Configuration;

/// <summary>M12 §14.2 (T-1202): the <c>OpenRouter</c> section replaces <c>Hermes</c>; the key never reaches a log or a file.</summary>
public sealed class OpenRouterSettingsTests : IDisposable
{
    private const string Marker = "sk-or-TESTMARKER123";

    private readonly ApiFactory _factory = new();
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        _factory.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Should_BindOpenRouterSection_When_Configured()
    {
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["OpenRouter:Enabled"] = "true",
            ["OpenRouter:BaseUrl"] = "https://or.example.test/api/v1",
            ["OpenRouter:ApiKey"] = Marker,
            ["OpenRouter:Model"] = "vendor/model-x",
            ["OpenRouter:TimeoutSeconds"] = "45",
            ["OpenRouter:Referer"] = "https://qb.example.test",
            ["OpenRouter:Title"] = "QbAutopost",
        });

        var settings = host.Services.GetRequiredService<IOptions<AppSettings>>().Value.OpenRouter;
        var options = host.Services.GetRequiredService<HermesOptions>();

        Assert.True(settings.Enabled);
        Assert.Equal("https://or.example.test/api/v1", settings.BaseUrl);
        Assert.Equal(45, settings.TimeoutSeconds);
        Assert.Equal("https://or.example.test/api/v1", options.BaseUrl);
        Assert.Equal(Marker, options.ApiKey);
        Assert.Equal("vendor/model-x", options.Model);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
        Assert.Equal("https://qb.example.test", options.Referer);
        Assert.Equal("QbAutopost", options.Title);
        Assert.Equal("https://or.example.test/api/v1/chat/completions", options.CompletionsUri.ToString());
    }

    [Fact]
    public void Should_DefaultToOpenRouter_When_NothingConfigured()
    {
        var s = new OpenRouterSettings();

        Assert.False(s.Enabled);
        Assert.Equal("https://openrouter.ai/api/v1", s.BaseUrl);
        Assert.Equal("", s.ApiKey);
        Assert.Equal("~google/gemini-pro-latest", s.Model);
        Assert.Equal(120, s.TimeoutSeconds);
        Assert.Null(s.Referer);
        Assert.Null(s.Title);
    }

    [Fact]
    public void Should_FailStartup_When_BaseUrlIsHttpAndNotLoopback()
    {
        using var host = _factory.WithSetting("OpenRouter:BaseUrl", "http://example.com");

        var ex = Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains("OpenRouter:BaseUrl", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9")]
    [InlineData("http://localhost:9/api/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    public void Should_AcceptBaseUrl_When_HttpsOrLoopback(string url)
    {
        var result = new OpenRouterOptionsValidator().Validate(
            null,
            new AppSettings { OpenRouter = { BaseUrl = url } });

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void Should_WarnOnce_When_HermesSectionPresent()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hermes:BaseUrl"] = "http://127.0.0.1:8642",
                ["Hermes:Model"] = "default",
            })
            .Build();
        var log = new ListLogger<OpenRouterSettingsTests>();

        LegacyHermesSection.Warn(config, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("OpenRouter", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NotWarn_When_NoHermesSection()
    {
        var log = new ListLogger<OpenRouterSettingsTests>();

        LegacyHermesSection.Warn(new ConfigurationBuilder().Build(), log);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Should_TakeBaseUrlFromOpenRouter_When_HermesSectionAlsoPresent()
    {
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["Hermes:BaseUrl"] = "http://old-hermes.invalid:8642",
            ["Hermes:Model"] = "old-model",
            ["OpenRouter:BaseUrl"] = "https://or.example.test/api/v1",
        });

        var options = host.Services.GetRequiredService<HermesOptions>();

        Assert.Equal("https://or.example.test/api/v1", options.BaseUrl);
        Assert.NotEqual("old-model", options.Model);
    }

    [Fact]
    public void Should_ScrubKey_When_OpenRouterKeyLogged()
    {
        using var host = _factory.WithSetting("OpenRouter:ApiKey", Marker);
        var log = host.Services.GetRequiredService<ILogger<OpenRouterSettingsTests>>();

        log.LogError(new HttpRequestException("401 for key " + Marker), "OpenRouter call failed with " + Marker);

        var dir = _factory.Dir.Combine("data", "logs");
        var file = Assert.Single(Directory.GetFiles(dir, LoggingSetup.FilePrefix + "*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains("OpenRouter call failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ListOpenRouterKey_When_SecretValuesAreCollected()
    {
        var settings = new AppSettings { OpenRouter = { ApiKey = Marker } };

        var values = LoggingSetup.SecretValues(new ConfigurationBuilder().Build(), settings);

        Assert.Contains(Marker, values);
    }

    [Fact]
    public void Should_WarnSecretInJson_When_OpenRouterKeyInAppsettings()
    {
        var path = _dir.Combine("appsettings.json");
        File.WriteAllText(path, $$$"""{"OpenRouter":{"ApiKey":"{{{Marker}}}"}}""");
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var log = new ListLogger<OpenRouterSettingsTests>();

        SecretWarnings.Warn(config, log);

        var entry = Assert.Single(log.Entries);
        Assert.Contains("OpenRouter:ApiKey", entry.Message, StringComparison.Ordinal);
        Assert.Contains("QBAUTOPOST__OpenRouter__ApiKey", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ShipWithoutHermesSectionOrKey_When_ReadingAppsettingsJson()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        Assert.False(config.GetSection("Hermes").Exists());
        Assert.True(config.GetSection("OpenRouter").Exists());
        Assert.True(string.IsNullOrEmpty(config["OpenRouter:ApiKey"]));
    }
}
