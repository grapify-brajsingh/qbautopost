using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.Api;

/// <summary><c>GET /health/quickbooks</c> (spec §6, FR-16) through the fake company.</summary>
public sealed class QuickBooksHealthApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public QuickBooksHealthApiTests() => _client = _factory.CreateAuthorizedClient(); // T-910: /health/quickbooks opens a QuickBooks session, so it needs health:read

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get()
    {
        using var response = await _client.GetAsync("/health/quickbooks");
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Should_Return200WithCompanyFile_When_HostQuerySucceeds()
    {
        _factory.Gateway.Company.ProductName = "QuickBooks Desktop Pro 2023";

        var (status, body) = await Get();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(@"C:\fake\Tropicana.QBW", body.GetProperty("companyFile").GetString());
        Assert.Equal("QuickBooks Desktop Pro 2023 33.0", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Should_SendOneHostQuery_When_Checked()
    {
        await Get();

        var request = Assert.Single(_factory.Gateway.Requests);
        Assert.Contains("<HostQueryRq requestID=\"1\" />", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Return503WithReason_When_QuickBooksIsUnavailable()
    {
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("could not open a QuickBooks session");

        var (status, body) = await Get();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.StartsWith("could not open a QuickBooks session", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Return503_When_HostQueryHangsPastBusyTimeout()
    {
        _factory.Gateway.QueryHang = true;
        using var client = _factory.WithSetting("QuickBooks:BusyTimeoutSeconds", "1").CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.GetAsync("/health/quickbooks");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("quickbooks-busy", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_StayOk_When_OnlyCompanyFileNameFails()
    {
        _factory.Gateway.CompanyFileThrow = new QuickBooksUnavailableException("no file name");

        var (status, body) = await Get();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("companyFile").ValueKind);
    }
}
