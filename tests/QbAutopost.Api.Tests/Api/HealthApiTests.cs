using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Api.Tests.Api;

/// <summary><c>GET /health/hermes</c> (spec §6, FR-16, plan T-204).</summary>
public sealed class HealthApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Should_Return200WithoutApiKey_When_HermesIsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/hermes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("fake-model", body.GetProperty("model").GetString());
        Assert.Equal(1, body.GetProperty("latencyMs").GetInt64());
    }

    [Fact]
    public async Task Should_Return503WithReason_When_FakeIsToldToFail()
    {
        _factory.Hermes.Ping = new HermesPing(false, "fake-model", 7, "HTTP 502");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/hermes");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("HTTP 502", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Should_PingConfiguredHermesThroughRealClient_When_HostBuildsIt()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""");
        using var factory = WithRealClient(handler);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/hermes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("configured-model", body.GetProperty("model").GetString());
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://hermes.configured:9999/v1/chat/completions", sent.Uri);
        Assert.Equal("Bearer sk-configured-key-0001", sent.Authorization);
        Assert.Equal(HermesClient.PingMaxTokens, JsonDocument.Parse(sent.Body).RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Should_Return503WithoutLeakingKey_When_RealHermesAnswersError()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, """{ "error": "bad key sk-configured-key-0001" }""");
        using var factory = WithRealClient(handler);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/hermes");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("HTTP 500", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-configured-key-0001", text, StringComparison.Ordinal);
    }

    /// <summary>The host's own <see cref="HermesClient"/> registration and settings, with the network replaced by <paramref name="handler"/>.</summary>
    private WebApplicationFactory<Program> WithRealClient(RecordingHandler handler) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hermes:BaseUrl"] = "http://hermes.configured:9999",
                ["Hermes:ApiKey"] = "sk-configured-key-0001",
                ["Hermes:Model"] = "configured-model",
            }));
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHermesClient>();
                services.AddHttpClient<IHermesClient, HermesClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
