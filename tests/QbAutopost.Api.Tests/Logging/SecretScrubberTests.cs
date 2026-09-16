using QbAutopost.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace QbAutopost.Api.Tests.Logging;

/// <summary>Spec §14 / CLAUDE.md rule 6: no secret reaches a log sink.</summary>
public sealed class SecretScrubberTests
{
    private const string ApiKey = "api-key-5f2c9e";
    private const string HermesKey = "sk-hermes-81b7aa";

    private readonly List<LogEvent> _events = [];

    private Logger CreateLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With<JobIdEnricher>()
            .WriteTo.Sink(new ScrubbingSink(new ListSink(_events), new SecretScrubber([ApiKey, HermesKey, "", null, "dev"])))
            .CreateLogger();

    private string Rendered()
    {
        var e = Assert.Single(_events);
        return e.RenderMessage() + "|" + e.Exception;
    }

    [Fact]
    public void Should_MaskConfiguredSecret_When_ItIsAPropertyValue()
    {
        using (var log = CreateLogger())
        {
            log.Information("calling with {Header}", "prefix " + HermesKey);
        }

        Assert.DoesNotContain(HermesKey, Rendered(), StringComparison.Ordinal);
        Assert.Contains("prefix ***", Rendered(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MaskConfiguredSecret_When_ItIsInTheTemplateText()
    {
        using (var log = CreateLogger())
        {
#pragma warning disable CA2254 // the point of the test is a secret baked into the template
            log.Information("key is " + ApiKey);
#pragma warning restore CA2254
        }

        Assert.Equal("key is ***|", Rendered());
    }

    [Fact]
    public void Should_MaskSecret_When_ItIsInAnExceptionMessage()
    {
        using (var log = CreateLogger())
        {
            log.Error(new InvalidOperationException("header X-Api-Key " + ApiKey + " refused"), "failed");
        }

        var text = Rendered();
        Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Equal("header X-Api-Key *** refused", _events[0].Exception!.Message);
    }

    [Fact]
    public void Should_KeepSameException_When_ItHoldsNoSecret()
    {
        var ex = new InvalidOperationException("plain");
        using (var log = CreateLogger())
        {
            log.Error(ex, "failed");
        }

        Assert.Same(ex, Assert.Single(_events).Exception);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("ApiKey")]
    [InlineData("HermesApiKey")]
    [InlineData("Password")]
    public void Should_MaskProperty_When_ItsNameLooksSecret(string name)
    {
        using (var log = CreateLogger())
        {
            log.ForContext(name, "unknown-value-123").Information("hello");
        }

        Assert.Equal("***", ((ScalarValue)Assert.Single(_events).Properties[name]).Value);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer ***")]
    [InlineData("X-Api-Key: other-unknown-key", "X-Api-Key: ***")]
    [InlineData("apiKey=abc123 next", "apiKey=*** next")]
    [InlineData("{\"ApiKey\": \"zzz999\"}", "{\"ApiKey\": \"***\"}")]
    [InlineData("API_SERVER_KEY=qwerty", "API_SERVER_KEY=***")]
    public void Should_MaskValue_When_ItFollowsASecretLabel(string input, string expected)
    {
        Assert.Equal(expected, new SecretScrubber([]).Scrub(input));
    }

    [Fact]
    public void Should_MaskNestedValues_When_ObjectIsDestructured()
    {
        using (var log = CreateLogger())
        {
            log.Information("settings {@Settings}", new { BaseUrl = "http://127.0.0.1:8642", ApiKey = "nested-unknown", Note = "k " + HermesKey });
        }

        var text = Rendered();
        Assert.DoesNotContain("nested-unknown", text, StringComparison.Ordinal);
        Assert.DoesNotContain(HermesKey, text, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:8642", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MaskDictionaryEntries_When_KeyLooksSecret()
    {
        using (var log = CreateLogger())
        {
            log.Information("headers {Headers}", new Dictionary<string, string> { ["Authorization"] = "plain-token", ["Accept"] = "json" });
        }

        var text = Rendered();
        Assert.DoesNotContain("plain-token", text, StringComparison.Ordinal);
        Assert.Contains("json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NotMaskShortSecretsByValue_When_TheyWouldGarbleWords()
    {
        using (var log = CreateLogger())
        {
            log.Information("{Environment}", "Development");
        }

        Assert.Equal("\"Development\"|", Rendered());
    }

    [Fact]
    public void Should_KeepEventUnchanged_When_NothingIsSecret()
    {
        using (var log = CreateLogger())
        {
            log.Information("Job {JobId}: {Action} started", "2026-08-tropicana", "Analyse");
        }

        Assert.Equal("Job \"2026-08-tropicana\": \"Analyse\" started|", Rendered());
    }

    [Fact]
    public void Should_UseNamedJobIdAsCorrelationId_When_NoScopeIsSet()
    {
        using (var log = CreateLogger())
        {
            log.Information("Job {JobId}: started", "job-7");
            log.Information("no job here");
            log.ForContext("jobId", "scoped").Information("Job {JobId}: other", "job-8");
        }

        Assert.Equal(
            ["job-7", JobIdEnricher.None, "scoped"],
            _events.Select(e => ((ScalarValue)e.Properties["jobId"]).Value));
    }

    private sealed class ListSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
