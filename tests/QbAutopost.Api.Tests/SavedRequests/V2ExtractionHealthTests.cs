using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212b (dashboard-api-m12 8, 9, 14.1): <c>GET /api/v2/health</c> with the <c>extraction</c> and <c>model</c> entries
/// (from settings only, never a call to OpenRouter), v1 health unchanged, and the upload route in the v2 document.
/// </summary>
public sealed class V2ExtractionHealthTests : IDisposable
{
    private readonly List<ExtractionTestHost> _hosts = [];

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private ExtractionTestHost Host(params (string Key, string? Value)[] settings)
    {
        var host = new ExtractionTestHost(settings.ToDictionary(p => p.Key, p => p.Value));
        _hosts.Add(host);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Theory]
    [InlineData("true", "true", "sk-x", "configured")]
    [InlineData("false", "true", "sk-x", "disabled")]
    [InlineData("true", "true", "", "misconfigured")]
    [InlineData("true", "false", "sk-x", "misconfigured")]
    public async Task Should_ReportExtractionState_When_V2HealthRead(string extraction, string openRouter, string key, string expected)
    {
        var host = Host(("Extraction:Enabled", extraction), ("OpenRouter:Enabled", openRouter), ("OpenRouter:ApiKey", key));
        using var client = host.User(DashboardRoles.Viewer);

        var (status, body) = await GetAsync(client, "/api/v2/health");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body.GetProperty("extraction").GetString());
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_ReportJobModel_When_V2HealthRead()
    {
        var host = Host(("OpenRouter:Model", "acme/jobs-1"));
        using var client = host.User(DashboardRoles.Viewer);

        var (_, body) = await GetAsync(client, "/api/v2/health");

        var model = body.GetProperty("model");
        Assert.Equal("enabled", model.GetProperty("status").GetString());
        Assert.Equal("acme/jobs-1", model.GetProperty("name").GetString());
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("MARKERKEY", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReportModelDisabled_When_OpenRouterOff()
    {
        var host = Host(("OpenRouter:Enabled", "false"));
        using var client = host.User(DashboardRoles.Viewer);

        var (_, body) = await GetAsync(client, "/api/v2/health");

        Assert.Equal("disabled", body.GetProperty("model").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Should_KeepV1HealthUnchanged_When_ExtractionConfigured()
    {
        var host = Host();
        using var client = host.App.CreateClient();

        var (status, body) = await GetAsync(client, "/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.TryGetProperty("extraction", out _));
        Assert.False(body.TryGetProperty("model", out _));
        Assert.Equal("enabled", body.GetProperty("hermes").GetString());
    }

    [Fact]
    public async Task Should_Return401_When_V2HealthAnonymous()
    {
        var host = Host();
        using var client = host.App.CreateClient();

        var (status, _) = await GetAsync(client, "/api/v2/health");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Should_DocumentRoute_When_OpenApiV2Read()
    {
        using var factory = new ApiFactory();
        using WebApplicationFactory<Program> app = factory.WithSetting("Api:Reference:Enabled", "true");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        var (status, document) = await GetAsync(client, "/api/v2/openapi.json");

        Assert.Equal(HttpStatusCode.OK, status);
        var post = document.GetProperty("paths").GetProperty("/api/v2/statement-extractions").GetProperty("post");
        Assert.Equal("requests:submit", post.GetProperty("x-required-scope").GetString());
        Assert.True(post.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
        var text = post.GetRawText();
        foreach (var type in new[]
                 {
                     "extraction-unavailable", "file-too-large", "not-a-pdf", "pdf-protected", "no-text", "not-a-bank-statement",
                     "extraction-failed", "extraction-timeout", "too-many-extractions",
                 })
        {
            Assert.Contains(type, text, StringComparison.Ordinal);
        }

        Assert.True(document.GetProperty("components").GetProperty("schemas").TryGetProperty("StatementExtraction", out _));
        Assert.True(document.GetProperty("paths").GetProperty("/api/v2/health").TryGetProperty("get", out _));
    }
}
