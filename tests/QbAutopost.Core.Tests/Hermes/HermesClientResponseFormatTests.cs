using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Core.Tests.Hermes;

/// <summary>M12 T-1202: <c>response_format</c>, a per-call model and the OpenRouter attribution headers.</summary>
public sealed class HermesClientResponseFormatTests : IDisposable
{
    private const string ValidJson = """{ "name": "Home Depot", "count": 2 }""";

    private readonly CapturingHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    [Fact]
    public async Task Should_SendResponseFormat_When_FormatGiven()
    {
        var format = new ResponseFormat("extraction", new JsonObject { ["type"] = "object" }, Strict: true);

        await Client().CompleteJsonAsync<Answer>(Request() with { Format = format }, CancellationToken.None);

        var rf = _handler.Json.GetProperty("response_format");
        Assert.Equal("json_schema", rf.GetProperty("type").GetString());
        var js = rf.GetProperty("json_schema");
        Assert.Equal("extraction", js.GetProperty("name").GetString());
        Assert.True(js.GetProperty("strict").GetBoolean());
        Assert.Equal("object", js.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_OmitResponseFormat_When_FormatNull()
    {
        await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.False(_handler.Json.TryGetProperty("response_format", out _));
        var expected = JsonNode.Parse(
            """{"model":"test-model","temperature":0,"messages":[{"role":"system","content":"You extract facts."},{"role":"user","content":"the requirement"}]}""");
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(_handler.Body)), _handler.Body);
    }

    [Fact]
    public async Task Should_UseRequestModel_When_ModelGiven()
    {
        await Client().CompleteJsonAsync<Answer>(Request() with { Model = "vendor/other" }, CancellationToken.None);

        Assert.Equal("vendor/other", _handler.Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Should_UseOptionsModel_When_RequestModelNull()
    {
        await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Equal("test-model", _handler.Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Should_SendAttributionHeaders_When_RefererAndTitleSet()
    {
        await Client(referer: "https://qb.example.test", title: "QbAutopost")
            .CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Equal("https://qb.example.test", _handler.Header("HTTP-Referer"));
        Assert.Equal("QbAutopost", _handler.Header("X-Title"));
    }

    [Fact]
    public async Task Should_OmitAttributionHeaders_When_NotSet()
    {
        await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Null(_handler.Header("HTTP-Referer"));
        Assert.Null(_handler.Header("X-Title"));
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("http://127.0.0.1:8642", "http://127.0.0.1:8642/v1/chat/completions")]
    public void Should_BuildCompletionsUri_When_BaseUrlWithOrWithoutVersion(string baseUrl, string expected)
    {
        Assert.Equal(expected, new HermesOptions { BaseUrl = baseUrl }.CompletionsUri.ToString());
    }

    [Fact]
    public void Should_DefaultToOpenRouter_When_OptionsNotConfigured()
    {
        var o = new HermesOptions();

        Assert.Equal("https://openrouter.ai/api/v1", o.BaseUrl);
        Assert.Equal("~google/gemini-pro-latest", o.Model);
    }

    [Fact]
    public void Should_HaveExtractionTask_When_Enumerated()
    {
        Assert.True(Enum.IsDefined(HermesTask.Extraction));
    }

    private static HermesRequest Request() => new(HermesTask.Spec, "You extract facts.", "the requirement");

    private HermesClient Client(string? referer = null, string? title = null) =>
        new(
            new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            new HermesOptions
            {
                BaseUrl = "http://hermes.test:8642/",
                ApiKey = "sk-test-0123456789abcdef",
                Model = "test-model",
                Timeout = TimeSpan.FromSeconds(30),
                Referer = referer,
                Title = title,
            });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private HttpRequestMessage? _request;

        public string Body { get; private set; } = "";

        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        public string? Header(string name) =>
            _request!.Headers.TryGetValues(name, out var values) ? values.Single() : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var reply = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = ValidJson } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Answer : IValidatable
    {
        public string? Name { get; init; }

        public int Count { get; init; }

        public IReadOnlyList<string> Validate() =>
            string.IsNullOrWhiteSpace(Name) || Count <= 0 ? ["invalid"] : [];
    }
}
