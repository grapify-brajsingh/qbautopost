using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-904 / FR-A-4: <c>POST /api/v1/quickbooks/connection/test</c>. Needs a key (it talks to QuickBooks), reports every
/// step, and refuses a per-request company file unless the owner has explicitly allowed it (Q-50).
/// </summary>
public sealed class ConnectionTestApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(object? request = null)
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/connection/test", request ?? new { }, ApiFactory.Json);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Should_ReportTheSteps_When_QuickBooksAnswers()
    {
        var (status, body) = await PostAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ["waitForGateway", "hostQuery", "companyFile"],
            body.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.True(body.GetProperty("totalMs").GetInt64() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("product").GetString()));
    }

    [Fact]
    public async Task Should_Answer503_When_QuickBooksIsNotAvailable()
    {
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("QuickBooks is not running");

        var (status, body) = await PostAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("QuickBooks is not running", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Should_RequireApiKey_When_ConnectionTestIsRequested()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/connection/test", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Refuse_When_ACompanyFileIsGivenButOverrideIsOff()
    {
        var (status, body) = await PostAsync(new { companyFile = @"C:\elsewhere\Other.QBW" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("AllowCompanyFileOverride", body.GetProperty("detail").GetString());
        // Nothing may be sent to QuickBooks when the request itself is refused.
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Refuse_When_TheTimeoutIsAboveTheCap()
    {
        var (status, body) = await PostAsync(new { timeoutSeconds = 9999 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("timeoutSeconds", body.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_SkipTheCompanyFile_When_CompanyInfoIsNotWanted()
    {
        var (status, body) = await PostAsync(new { includeCompanyInfo = false });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            ["waitForGateway", "hostQuery"],
            body.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
    }
}
