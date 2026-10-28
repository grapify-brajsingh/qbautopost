using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Hosting;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Hosting;

/// <summary>
/// T-1116 (dashboard v2 spec 13.1, R-2): the built dashboard is served from <c>Dashboard:Path</c> on every path that is
/// not the API's, with an <c>index.html</c> fallback for client routes, and every <c>/api</c> and legacy flat path
/// answers exactly as it did before (O-1).
/// </summary>
public sealed class DashboardHostingTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><html><head><title>QbAutopost</title></head><body>dashboard-index</body></html>";
    private const string AppJs = "console.log('dashboard-asset');";
    private const string DotEnv = "SECRET=do-not-serve";

    private readonly ApiFactory _factory = new();
    private readonly List<IDisposable> _hosts = [];

    public DashboardHostingTests()
    {
        Directory.CreateDirectory(_factory.Dir.Combine("dashboard", "assets"));
        File.WriteAllText(_factory.Dir.Combine("dashboard", "index.html"), IndexHtml);
        File.WriteAllText(_factory.Dir.Combine("dashboard", "assets", "app-abc123.js"), AppJs);
        File.WriteAllText(_factory.Dir.Combine("dashboard", ".env"), DotEnv);
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        _factory.Dispose();
    }

    [Fact]
    public async Task Should_ServeIndex_When_RootRequested()
    {
        using var client = Installed().CreateClient();

        using var response = await client.GetAsync("/");

        await AssertIndexAsync(response);
    }

    [Fact]
    public async Task Should_ServeIndex_When_DeepLinkRequested()
    {
        using var client = Installed().CreateClient();

        using var response = await client.GetAsync("/requests/123");

        await AssertIndexAsync(response);
    }

    [Fact]
    public async Task Should_ServeIndexWithHtmlHeaders_When_IndexRequestedDirectly()
    {
        using var client = Installed().CreateClient();

        using var response = await client.GetAsync("/index.html");

        await AssertIndexAsync(response);
    }

    [Fact]
    public async Task Should_AnswerWithoutBody_When_HeadRequested()
    {
        using var client = Installed().CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/requests/123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DashboardHosting.HtmlCacheControl, response.Headers.CacheControl?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Should_CacheImmutable_When_AssetRequested()
    {
        using var client = Installed().CreateClient();

        using var response = await client.GetAsync("/assets/app-abc123.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AppJs, await response.Content.ReadAsStringAsync());
        Assert.Equal(DashboardHosting.AssetCacheControl, response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Theory]
    [InlineData("/.env")]
    [InlineData("/.ENV")]
    [InlineData("/assets/../.env")]
    public async Task Should_NotServe_When_Dotfile(string path)
    {
        using var client = Installed().CreateClient();

        using var response = await client.GetAsync(path);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("do-not-serve", body, StringComparison.Ordinal);
        Assert.Equal(IndexHtml, body);
    }

    [Theory]
    [InlineData("POST", "/requests")]
    [InlineData("PUT", "/")]
    [InlineData("DELETE", "/assets/app-abc123.js")]
    [InlineData("POST", "/index.html")]
    public async Task Should_Answer405_When_PostToDashboardPath(string method, string path)
    {
        using var client = Installed().CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.DoesNotContain("dashboard-index", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// O-1: each legacy flat path, with and without a key, answers with the same status and content type as on a host
    /// that does not serve the dashboard at all.
    /// </summary>
    [Theory]
    [InlineData("GET", "/jobs", false)]
    [InlineData("GET", "/jobs", true)]
    [InlineData("GET", "/health", false)]
    [InlineData("GET", "/health/ready", false)]
    [InlineData("GET", "/batches/unknown-batch", true)]
    [InlineData("GET", "/quickbooks/health", false)]
    [InlineData("POST", "/qb/sync-lists", false)]
    [InlineData("GET", "/rules/unknown", true)]
    [InlineData("GET", "/jobs/no-such-job", true)]
    [InlineData("GET", "/health/no-such-route", true)]
    public async Task Should_NotServeDashboard_When_PathIsLegacyFlatRoute(string method, string path, bool withKey)
    {
        var served = await SendAsync(Installed(), method, path, withKey);
        var before = await SendAsync(Disabled(), method, path, withKey);

        Assert.NotEqual("text/html", served.MediaType);
        Assert.DoesNotContain("dashboard-index", served.Body, StringComparison.Ordinal);
        Assert.Equal((before.Status, before.MediaType), (served.Status, served.MediaType));
    }

    [Fact]
    public void Should_CoverEveryLegacyRoute_When_PrefixesChecked()
    {
        var host = Installed();
        var flat = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .Where(p => !new PathString(p).StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        Assert.NotEmpty(flat);
        Assert.All(flat, route =>
        {
            var literal = new PathString(route.Split('{')[0].TrimEnd('/') is { Length: > 0 } l ? l : "/");
            Assert.Contains(ApiRoutes.LegacyFlatPrefixes, prefix => literal.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
            Assert.False(ApiRoutes.IsDashboardPath(literal), route);
        });
    }

    [Theory]
    [InlineData("/statements")]
    [InlineData("/requests")]
    [InlineData("/audit")]
    [InlineData("/users")]
    [InlineData("/login")]
    public async Task Should_NotClash_When_DashboardClientRoutesChecked(string route)
    {
        Assert.True(ApiRoutes.IsDashboardPath(route));

        using var client = Installed().CreateClient();
        using var response = await client.GetAsync(route + "/42");

        await AssertIndexAsync(response);
    }

    [Theory]
    [InlineData("/healthy", true)]
    [InlineData("/jobsite", true)]
    [InlineData("/qbx", true)]
    [InlineData("/apis", true)]
    [InlineData("/", true)]
    [InlineData("/health", false)]
    [InlineData("/health/x", false)]
    [InlineData("/jobs/1", false)]
    [InlineData("/JOBS", false)]
    [InlineData("/Batches/b1/undo", false)]
    [InlineData("/qb/sync-lists", false)]
    [InlineData("/quickbooks/lists", false)]
    [InlineData("/rules/vendors", false)]
    [InlineData("/api", false)]
    [InlineData("/API/v1/jobs", false)]
    [InlineData("/api/v2/saved-requests", false)]
    [InlineData("/api/v3/anything", false)]
    public void Should_MatchBySegment_When_DashboardPathChecked(string path, bool dashboard) =>
        Assert.Equal(dashboard, ApiRoutes.IsDashboardPath(path));

    [Theory]
    [InlineData("/healthy")]
    [InlineData("/jobsite")]
    public async Task Should_ServeIndex_When_PathOnlySharesLettersWithLegacyPrefix(string path)
    {
        using var client = Installed().CreateClient();

        using var response = await client.GetAsync(path);

        await AssertIndexAsync(response);
    }

    [Theory]
    [InlineData("GET", "/api/v1/no-such-route", false)]
    [InlineData("GET", "/api/v1/no-such-route", true)]
    [InlineData("GET", "/api/v2/no-such-route", false)]
    [InlineData("GET", "/api/v2/no-such-route", true)]
    [InlineData("GET", "/api/no-such-route", true)]
    [InlineData("GET", "/api", true)]
    [InlineData("GET", "/API/V1/no-such-route", true)]
    [InlineData("POST", "/api/v1/no-such-route", true)]
    [InlineData("GET", "/api/v1/index.html", true)]
    [InlineData("GET", "/api/v1/assets/app-abc123.js", true)]
    public async Task Should_NeverReturnHtml_When_UnknownApiPath(string method, string path, bool withKey)
    {
        var served = await SendAsync(Installed(), method, path, withKey);
        var before = await SendAsync(Disabled(), method, path, withKey);

        Assert.NotEqual("text/html", served.MediaType);
        Assert.DoesNotContain("dashboard-index", served.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("dashboard-asset", served.Body, StringComparison.Ordinal);
        Assert.Equal((before.Status, before.MediaType, before.Body), (served.Status, served.MediaType, served.Body));
    }

    /// <summary>Ruling R14: flat <c>/audit-events</c> is neither under <c>/api</c> nor under a legacy prefix.</summary>
    [Theory]
    [InlineData("/audit-events")]
    [InlineData("/audit-events.csv")]
    public async Task Should_ServeDashboard_When_FlatAuditEventsPathRequested(string path)
    {
        using var installed = Installed().CreateClient();
        using var missing = Track(_factory.WithSetting("Dashboard:Path", _factory.Dir.Combine("no-such-dashboard"))).CreateClient();

        using var served = await installed.GetAsync(path);
        using var notInstalled = await missing.GetAsync(path);

        await AssertIndexAsync(served);
        Assert.Equal(HttpStatusCode.NotFound, notInstalled.StatusCode);
        Assert.Equal("text/plain", notInstalled.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DashboardHosting.NotInstalledText, await notInstalled.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Should_Answer404PlainText_When_FolderMissing()
    {
        var host = Track(_factory.WithSetting("Dashboard:Path", _factory.Dir.Combine("no-such-dashboard")));
        using var client = host.CreateClient();

        using var root = await client.GetAsync("/requests/1");
        using var health = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal("text/plain", root.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DashboardHosting.NotInstalledText, await root.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Should_Answer404PlainText_When_FolderEmpty()
    {
        Directory.CreateDirectory(_factory.Dir.Combine("empty-dashboard"));
        var host = Track(_factory.WithSetting("Dashboard:Path", _factory.Dir.Combine("empty-dashboard")));
        using var client = host.CreateClient();

        using var root = await client.GetAsync("/");
        using var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal("text/plain", root.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DashboardHosting.NotInstalledText, await root.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Should_EmitNoCorsHeaders_When_SameOriginCall()
    {
        using var client = Installed().CreateClient();
        using var page = new HttpRequestMessage(HttpMethod.Get, "/requests");
        page.Headers.Add("Origin", "http://localhost");
        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        api.Headers.Add("Origin", "http://localhost");

        using var pageResponse = await client.SendAsync(page);
        using var apiResponse = await client.SendAsync(api);

        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.DoesNotContain(pageResponse.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(apiResponse.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Should_LeaveRootToApi_When_DashboardDisabled()
    {
        var host = Track(_factory.WithSettings(new Dictionary<string, string?>
        {
            ["Dashboard:Path"] = _factory.Dir.Combine("dashboard"),
            ["Dashboard:Enabled"] = "false",
        }));
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/requests/123");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("dashboard-index", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ShipDefaults_When_NobodyConfiguresTheDashboard()
    {
        var settings = new QbAutopost.Api.Configuration.DashboardSettings();

        Assert.Equal((@"C:\qb-autopost\dashboard", true), (settings.Path, settings.Enabled));
    }

    private static async Task AssertIndexAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
        Assert.Equal(DashboardHosting.HtmlCacheControl, response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'",
            Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static async Task<(HttpStatusCode Status, string? MediaType, string Body)> SendAsync(
        WebApplicationFactory<Program> host, string method, string path, bool withKey)
    {
        using var client = host.CreateClient();
        if (withKey)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        }

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, Normalise(body));
    }

    /// <summary>Problem details carry a per-request correlation id; it is the only part that may differ.</summary>
    private static string Normalise(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"(requestId|traceId)\":\"[^\"]*\"", "\"$1\":\"\"");

    private WebApplicationFactory<Program> Installed() =>
        Track(_factory.WithSetting("Dashboard:Path", _factory.Dir.Combine("dashboard")));

    private WebApplicationFactory<Program> Disabled() =>
        Track(_factory.WithSettings(new Dictionary<string, string?>
        {
            ["Dashboard:Path"] = _factory.Dir.Combine("dashboard"),
            ["Dashboard:Enabled"] = "false",
        }));

    private WebApplicationFactory<Program> Track(WebApplicationFactory<Program> host)
    {
        _hosts.Add(host);
        return host;
    }
}
