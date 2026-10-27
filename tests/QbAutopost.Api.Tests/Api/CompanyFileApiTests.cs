using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-905 / FR-A-5: <c>POST /api/v1/quickbooks/company-file/validate</c>. 200 when every error check passes, 422 when
/// one fails — with the same body either way, because a validator that only explains itself on success is useless.
/// </summary>
public sealed class CompanyFileApiTests : IDisposable
{
    /// <summary>The file the fake gateway reports as the one QuickBooks has open.</summary>
    private const string OpenCompanyFile = @"C:\fake\Tropicana.QBW";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        WebApplicationFactory<Program>? host = null, object? request = null)
    {
        using var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/company-file/validate", request ?? new { }, ApiFactory.Json);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static JsonElement Check(JsonElement body, string name) =>
        body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Should_ReportTheCheck_When_NoCompanyFileIsConfigured()
    {
        // Set explicitly: the test host otherwise inherits Company:FilePath from the shipped appsettings.json.
        using var host = _factory.WithSetting("Company:FilePath", string.Empty);

        var (status, body) = await PostAsync(host);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.False(Check(body, "configured").GetProperty("ok").GetBoolean());
        // Nothing else is claimed once there is no path to judge.
        Assert.Single(body.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Should_Fail_When_QuickBooksHasADifferentFileOpen()
    {
        using var host = _factory.WithSetting("Company:FilePath", @"C:\fake\Another.QBW");

        var (status, body) = await PostAsync(host);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var open = Check(body, "openInQuickBooks");
        Assert.False(open.GetProperty("ok").GetBoolean());
        Assert.Contains("Tropicana.QBW", open.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Should_RequireApiKey_When_ValidationIsRequested()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/company-file/validate", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Refuse_When_ACompanyFileIsGivenButOverrideIsOff()
    {
        var (status, body) = await PostAsync(request: new { companyFile = @"C:\elsewhere\Other.QBW" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("AllowCompanyFileOverride", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Should_SendNoQbXml_When_ValidationRuns()
    {
        using var host = _factory.WithSetting("Company:FilePath", OpenCompanyFile);

        await PostAsync(host);

        // FR-A-5 is read-only: asking which file is open is allowed, sending a request is not.
        Assert.Empty(_factory.Gateway.Requests);
        Assert.True(_factory.Gateway.CompanyFileCalls >= 1);
    }

    [Fact]
    public async Task Should_NameTheOpenFile_When_TheConfiguredFileDoesNotExist()
    {
        // The configured path is the one the fake reports as open, but no such file exists on this machine:
        // "QuickBooks has it open" and "it is on disk" are different questions and are answered separately.
        using var host = _factory.WithSetting("Company:FilePath", OpenCompanyFile);

        var (status, body) = await PostAsync(host);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.False(Check(body, "exists").GetProperty("ok").GetBoolean());
        Assert.True(Check(body, "openInQuickBooks").GetProperty("ok").GetBoolean());
    }
}
