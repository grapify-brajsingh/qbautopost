using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-902 / FR-A-1, FR-A-2: <c>GET /api/v1/health</c> answers from in-process state only, so a monitor can poll it
/// every few seconds without queueing behind a post, and <c>/health/ready</c> says whether work can be accepted.
/// </summary>
public sealed class AppHealthApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetAsync(string path, HttpStatusCode expected)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Should_ReportHealthy_When_HostIsRunning()
    {
        var body = await GetAsync("/api/v1/health", HttpStatusCode.OK);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("Testing", body.GetProperty("environment").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Should_ReportTheProcess_When_HealthIsRequested()
    {
        var body = await GetAsync("/api/v1/health", HttpStatusCode.OK);

        var process = body.GetProperty("process");
        var bitness = process.GetProperty("bitness").GetString();
        Assert.True(bitness is "x64" or "x86" or "arm64", $"unexpected bitness '{bitness}'");
        Assert.True(process.GetProperty("uptimeSeconds").GetInt64() >= 0);
    }

    [Fact]
    public async Task Should_ReportTheGatewayAndHermesModes_When_HealthIsRequested()
    {
        var body = await GetAsync("/api/v1/health", HttpStatusCode.OK);

        // The test host injects a fake gateway (QbConnectionMode.Test); Hermes is enabled unless a test turns it off.
        Assert.Equal("test", body.GetProperty("quickbooksGateway").GetString());
        Assert.Equal("enabled", body.GetProperty("hermes").GetString());
    }

    [Fact]
    public async Task Should_ReportAnIdleWorker_When_NoJobIsRunning()
    {
        var body = await GetAsync("/api/v1/health", HttpStatusCode.OK);

        var worker = body.GetProperty("worker");
        Assert.True(worker.GetProperty("running").GetBoolean());
        Assert.Equal(JsonValueKind.Null, worker.GetProperty("activeJobId").ValueKind);
        Assert.Equal(0, worker.GetProperty("queueDepth").GetInt32());
    }

    [Fact]
    public async Task Should_NotTouchQuickBooksOrHermes_When_HealthIsRequested()
    {
        await GetAsync("/api/v1/health", HttpStatusCode.OK);
        await GetAsync("/api/v1/health/ready", HttpStatusCode.OK);

        Assert.Empty(_factory.Gateway.Requests);
        Assert.Empty(_factory.Hermes.Calls);
    }

    [Fact]
    public async Task Should_BeReady_When_RulesAreInPlace()
    {
        var body = await GetAsync("/api/v1/health/ready", HttpStatusCode.OK);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(body.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Should_ReportTheFailingCheck_When_RulesFileIsMissing()
    {
        using var host = _factory.WithSetting("Company:RulesFile", _factory.Dir.Combine("no-such-rules.json"));
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        var rules = body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "rules");
        Assert.False(rules.GetProperty("ok").GetBoolean());
    }
}
