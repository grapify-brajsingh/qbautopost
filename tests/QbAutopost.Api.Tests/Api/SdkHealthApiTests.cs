using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-903 / FR-A-3: <c>GET /api/v1/health/sdk</c> answers whether the QuickBooks SDK itself is usable — registration,
/// bitness, qbXML version — **without opening a company file**, so it still answers when QuickBooks is closed or a
/// certificate dialog is pending. It is the check that explains a failure before the app ever reaches QuickBooks.
/// </summary>
public sealed class SdkHealthApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetAsync(HttpStatusCode expected)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/health/sdk");
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Should_ReportReady_When_TheSdkIsHealthy()
    {
        var body = await GetAsync(HttpStatusCode.OK);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("requestProcessor").GetProperty("registered").GetBoolean());
        Assert.Equal("x64", body.GetProperty("processBitness").GetString());
        Assert.True(body.GetProperty("bitnessMatch").GetBoolean());
        Assert.Equal("16.0", body.GetProperty("qbXmlVersion").GetString());
    }

    [Fact]
    public async Task Should_Answer503_When_TheRequestProcessorIsNotRegistered()
    {
        _factory.SdkProbe.Info = FakeQbSdkProbe.Healthy with
        {
            Ok = false,
            RequestProcessor = new QbSdkRequestProcessor(false, "QBXMLRP2.RequestProcessor", null, false),
            Message = "QBXMLRP2.RequestProcessor is not registered; the QuickBooks SDK is not installed",
        };

        var body = await GetAsync(HttpStatusCode.ServiceUnavailable);

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.False(body.GetProperty("requestProcessor").GetProperty("registered").GetBoolean());
        Assert.Contains("not registered", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Should_Answer503_When_BitnessDoesNotMatch()
    {
        // The T-609 failure: an x86 host process cannot drive an x64 QuickBooks.
        _factory.SdkProbe.Info = FakeQbSdkProbe.Healthy with
        {
            Ok = false,
            ProcessBitness = "x86",
            QuickBooks = new QbSdkProcess(true, "QBW.EXE", "x64"),
            BitnessMatch = false,
            Message = "the process is x86 but QuickBooks is x64",
        };

        var body = await GetAsync(HttpStatusCode.ServiceUnavailable);

        Assert.False(body.GetProperty("bitnessMatch").GetBoolean());
        Assert.Equal("x86", body.GetProperty("processBitness").GetString());
        Assert.Equal("x64", body.GetProperty("quickBooks").GetProperty("bitness").GetString());
    }

    [Fact]
    public async Task Should_NotRequireApiKey_When_SdkHealthIsRequested()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/sdk");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_NotOpenACompanyFile_When_SdkHealthIsRequested()
    {
        await GetAsync(HttpStatusCode.OK);

        // FR-A-3: the probe never begins a session, so no qbXML and no company-file call may reach the gateway.
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Equal(0, _factory.Gateway.CompanyFileCalls);
        Assert.Equal(1, _factory.SdkProbe.Calls);
    }
}
