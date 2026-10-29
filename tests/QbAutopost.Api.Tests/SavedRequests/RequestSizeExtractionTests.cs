using System.Net;
using System.Net.Http.Headers;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212b (dashboard-api-m12 6.1 "Body size"): only the upload route may carry more than <c>Api:MaxRequestBodyBytes</c>;
/// it answers <c>413 file-too-large</c> over its own limit, and every other route keeps today's 2 MB and today's problem.
/// </summary>
public sealed class RequestSizeExtractionTests : IDisposable
{
    private readonly List<ExtractionTestHost> _hosts = [];

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private ExtractionTestHost Host(params (string Key, string? Value)[] settings)
    {
        var host = new ExtractionTestHost(settings.ToDictionary(p => p.Key, p => p.Value));
        _hosts.Add(host);
        return host;
    }

    private static ByteArrayContent Body(int bytes, string mediaType)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }

    [Fact]
    public async Task Should_KeepGlobalLimit_When_RouteIsNotExtraction()
    {
        var host = Host();
        using var client = host.User(DashboardRoles.Editor);

        using var response = await client.PostAsync("/api/v2/saved-requests", Body(3 * 1024 * 1024, "application/json"));
        var problem = await response.ReadProblemAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("Request body too large", problem.GetProperty("title").GetString());
        Assert.Contains("Api:MaxRequestBodyBytes", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(problem.TryGetProperty("type", out var type) && type.GetString() == "file-too-large");
    }

    [Fact]
    public async Task Should_Return413FileTooLarge_When_BodyOverExtractionLimit()
    {
        var host = Host(("Extraction:MaxBytes", "1000"));
        using var client = host.User(DashboardRoles.Editor);

        using var response = await client.PostAsync(ExtractionTestHost.Route, Body(1000 + 70 * 1024, "multipart/form-data; boundary=x"));
        var problem = await response.ReadProblemAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file-too-large", problem.GetProperty("type").GetString());
        Assert.Contains("1000 bytes", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_AcceptBodyOverGlobalLimit_When_RouteIsExtraction()
    {
        // The global limit below the PDF's size: only the upload route's own limit applies to it.
        var host = Host(("Api:MaxRequestBodyBytes", "1000"));
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        using var client = host.User(DashboardRoles.Editor);

        var (response, _) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_KeepGlobalLimit_When_ExtractionPathHasAnotherMethod()
    {
        var host = Host(("Api:MaxRequestBodyBytes", "1000"));
        using var client = host.User(DashboardRoles.Editor);

        using var response = await client.PutAsync(ExtractionTestHost.Route, Body(5000, "application/json"));
        var problem = await response.ReadProblemAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("Request body too large", problem.GetProperty("title").GetString());
    }

    [Fact]
    public void Should_RaiseServerLimit_When_ExtractionAllowsMore()
    {
        var api = new ApiSettings();
        var extraction = new ExtractionSettings();

        Assert.Equal(10485760 + 64 * 1024, RequestSizeMiddleware.ServerLimit(api, extraction));
        Assert.Equal(api.MaxRequestBodyBytes, RequestSizeMiddleware.LimitFor("POST", "/api/v2/saved-requests", api, extraction));
        Assert.Equal(10485760 + 64 * 1024, RequestSizeMiddleware.LimitFor("POST", "/api/v2/statement-extractions/", api, extraction));
        Assert.Equal(api.MaxRequestBodyBytes, RequestSizeMiddleware.LimitFor("POST", "/api/v1/statement-extractions", api, extraction));
    }
}
