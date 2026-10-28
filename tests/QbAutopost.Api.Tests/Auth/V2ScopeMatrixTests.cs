using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Core.Security;
using static QbAutopost.Api.Tests.Auth.CallerMatrixTests;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>
/// T-1109 (dashboard v2 spec 2, 3): every v2 route needs exactly the scope of its v1 twin, and is named in
/// <see cref="ApiRoutes.ScopeFor"/> rather than reaching it by a fall-through (the validate-and-post security trap,
/// repeated for <c>/api/v2</c>). The HTTP half runs every caller of <see cref="CallerMatrixTests"/> against the v1
/// and v2 forms of the same routes and asks for the same auth decision.
/// </summary>
public sealed class V2ScopeMatrixTests(Host host) : IClassFixture<Host>
{
    private const string V1 = "/api/v1";
    private const string V2 = "/api/v2";

    /// <summary>Every route of <see cref="ApiRoutes.V2Routes"/>, with its parameters filled in.</summary>
    public static TheoryData<string, string> V2Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, template) in ApiRoutes.V2Routes)
        {
            data.Add(method, Concrete(template));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(V2Routes))]
    public void Should_RequireSameScope_When_V2RouteCalled(string method, string relative)
    {
        // Routes that exist under /api/v2 only (the audit feed, T-1114) have no v1 twin: theirs is closed to admin.
        if (relative.StartsWith("/audit-events", StringComparison.Ordinal))
        {
            Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor(method, V1 + relative));
            Assert.Equal(ApiScopes.RequestsRead, ApiRoutes.ScopeFor(method, V2 + relative));
            return;
        }

        Assert.Equal(ApiRoutes.ScopeFor(method, V1 + relative), ApiRoutes.ScopeFor(method, V2 + relative));
        Assert.Equal(
            ApiRoutes.ScopeFor(method, V1 + relative, isDevelopment: true),
            ApiRoutes.ScopeFor(method, V2 + relative, isDevelopment: true));
        Assert.NotEqual(ApiScopes.Admin, ApiRoutes.ScopeFor(method, V2 + relative));
    }

    /// <summary>The fall-through stays <c>admin</c> for anything under <c>/api/v2</c> that spec 3 does not name.</summary>
    [Theory]
    [InlineData("GET", V2 + "/jobs")]
    [InlineData("POST", V2 + "/jobs")]
    [InlineData("GET", V2 + "/health")]
    [InlineData("GET", V2 + "/health/ready")]
    [InlineData("GET", V2 + "/reference")]
    [InlineData("POST", V2 + "/quickbooks/lists/sync")]
    [InlineData("POST", V2 + "/quickbooks/transactions")]
    [InlineData("POST", V2 + "/quickbooks/transactions/validate-and-post")]
    [InlineData("POST", V2 + "/quickbooks/connection/test")]
    [InlineData("POST", V2 + "/qb/sync-lists")]
    [InlineData("GET", V2 + "/batches/1")]
    [InlineData("POST", V2 + "/rules/alias")]
    [InlineData("GET", V2 + "/saved-requests/12/unknown")]
    [InlineData("GET", V2 + "/saved-requestsx")]
    [InlineData("GET", V2 + "/usersx")]
    [InlineData("POST", V2 + "/auth/anything")]
    [InlineData("GET", V2)]
    public void Should_CloseTheRoute_When_V2PathIsNotInTheTable(string method, string path) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor(method, path));

    private static readonly (string Method, string Relative)[] SpotRoutes =
    [
        ("POST", "/saved-requests"),
        ("GET", "/saved-requests"),
        ("PUT", "/saved-requests/1"),
        ("POST", "/saved-requests/1/post"),
        ("POST", "/saved-requests/1/resolve"),
        ("GET", "/users"),
        ("GET", "/quickbooks/lists"),
        ("GET", "/health/database"),
        ("GET", "/auth/me"),
    ];

    public static TheoryData<Who, string, string> SpotChecks()
    {
        var data = new TheoryData<Who, string, string>();
        foreach (var who in Enum.GetValues<Who>())
        {
            foreach (var (method, relative) in SpotRoutes)
            {
                data.Add(who, method, relative);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SpotChecks))]
    public async Task Should_DecideAuthAsV1Does_When_ACallerCallsAV2Route(Who who, string method, string relative)
    {
        using var v1Client = host.ClientFor(who);
        using var v2Client = host.ClientFor(who);

        using var v1 = await v1Client.SendAsync(Request(method, V1 + relative));
        using var v2 = await v2Client.SendAsync(Request(method, V2 + relative));

        Assert.Equal(await OutcomeOf(v1), await OutcomeOf(v2));
    }

    private static string Concrete(string template) =>
        template.Replace("{id}", "12", StringComparison.Ordinal)
            .Replace("{n}", "3", StringComparison.Ordinal)
            .Replace("{attemptId}", "7", StringComparison.Ordinal);

    /// <summary>The answer, read as far as the auth decision, as <see cref="CallerMatrixTests"/> reads it.</summary>
    private static async Task<Expect> OutcomeOf(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return Expect.Unauthorized;
            case HttpStatusCode.Forbidden:
                return await TypeOf(response) == "password-change-required" ? Expect.PasswordChangeRequired : Expect.Forbidden;
            case HttpStatusCode.BadRequest when await TypeOf(response) == "ambiguous-credentials":
                return Expect.Ambiguous;
            default:
                return Expect.Passed;
        }
    }

    private static async Task<string?> TypeOf(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return null;
        }

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { }),
        };
}
