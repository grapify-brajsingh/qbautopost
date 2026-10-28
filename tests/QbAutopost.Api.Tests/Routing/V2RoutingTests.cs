using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Database;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.SavedRequests;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Routing;

/// <summary>
/// T-1109 (dashboard v2 spec 2, 3, 13.1): the <c>/api/v2</c> group. It maps exactly the routes of spec 3, its
/// shared routes answer as their v1 twins, the database gate guards it, and an unknown path under it answers as an
/// unknown v1 path does: 401, 403 to a non-admin, JSON 404 to an admin, never HTML.
/// </summary>
public sealed class V2RoutingTests : IDisposable
{
    private const string V2 = "/api/v2";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Should_ServeSameBody_When_V1AndV2OriginalRead()
    {
        var id = await _host.SaveFixtureAsync("n8n-unicode.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var v1 = await viewer.GetAsync($"/api/v1/saved-requests/{id}/original");
        using var v2 = await viewer.GetAsync($"{V2}/saved-requests/{id}/original");

        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        Assert.Equal(v1.StatusCode, v2.StatusCode);
        Assert.Equal(v1.Content.Headers.ContentType?.ToString(), v2.Content.Headers.ContentType?.ToString());
        Assert.Equal(await v1.Content.ReadAsByteArrayAsync(), await v2.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Should_AnswerV2Location_When_SavedThroughV2()
    {
        using var n8n = _host.N8n();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{V2}/saved-requests")
        {
            Content = SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-good.json")),
        };

        using var response = await n8n.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
        Assert.Equal($"{V2}/saved-requests/{id}", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("GET", V2 + "/jobs")]
    [InlineData("GET", V2 + "/saved-requests/1/unknown")]
    [InlineData("POST", V2 + "/quickbooks/transactions/validate-and-post")]
    public async Task Should_Answer403_When_UnknownV2PathNonAdmin(string method, string path)
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(V2 + "/jobs")]
    [InlineData(V2 + "/saved-requests/1/unknown")]
    [InlineData(V2 + "/reference")]
    public async Task Should_Answer404Json_When_UnknownV2PathAdmin(string path)
    {
        using var admin = _host.Factory.CreateAuthorizedClient();

        using var response = await admin.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// An unknown POST answers a bodiless 404 on v1 today; spec 13.1 asks v2 to answer as v1 does, and never with HTML.
    /// </summary>
    [Theory]
    [InlineData("/quickbooks/transactions/validate-and-post")]
    [InlineData("/jobs")]
    public async Task Should_AnswerAsV1_When_UnknownV2PostAdmin(string relative)
    {
        using var admin = _host.Factory.CreateAuthorizedClient();

        using var v1 = await admin.PostAsync("/api/v1/unknown" + relative, null);
        using var v2 = await admin.PostAsync(V2 + relative, null);

        Assert.Equal(HttpStatusCode.NotFound, v2.StatusCode);
        Assert.Equal(v1.StatusCode, v2.StatusCode);
        Assert.Equal(v1.Content.Headers.ContentType?.MediaType, v2.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("text/html", v2.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("GET", V2 + "/jobs")]
    [InlineData("GET", V2 + "/health")]
    [InlineData("GET", V2 + "/saved-requests/1/unknown")]
    public async Task Should_Answer401_When_UnknownV2PathAnonymous(string method, string path)
    {
        using var anonymous = _host.Factory.CreateClient();

        using var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Should_Answer503_When_DatabaseDownOnV2()
    {
        _host.Factory.Gate.Set(DatabaseState.Unreachable);
        using var n8n = _host.N8n();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{V2}/saved-requests")
        {
            Content = SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-good.json")),
        };

        using var response = await n8n.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database-unavailable", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(DatabaseGateFilter.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers.RetryAfter?.ToString());
    }

    /// <summary>
    /// The group and <see cref="ApiRoutes.V2Routes"/> name the same routes, so a route added to one but not the other
    /// fails here rather than landing silently on <c>admin</c> or on a neighbour's scope.
    /// </summary>
    [Fact]
    public void Should_MapExactlyTheV2RouteTable_When_ReferenceEnabled()
    {
        using var host = _host.Factory.WithSetting("Api:Reference:Enabled", "true");
        var source = host.Services.GetRequiredService<EndpointDataSource>();

        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');
            if (!path.StartsWith(V2 + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                mapped.Add($"{method.ToUpperInvariant()} {path}");
            }
        }

        var table = ApiRoutes.V2Routes.Select(r => $"{r.Method} {V2}{r.Template}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(table.OrderBy(x => x, StringComparer.Ordinal), mapped.OrderBy(x => x, StringComparer.Ordinal));
    }
}
