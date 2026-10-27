using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>
/// T-1009 (saved-requests spec 11.1, 11.3): every kind of caller against the routes that decide who may move money.
/// Routes that do not exist yet are asserted only up to the auth decision: anything but 401, 403 or
/// <c>400 ambiguous-credentials</c> counts as "passed auth".
/// </summary>
public sealed class CallerMatrixTests(CallerMatrixTests.Host host) : IClassFixture<CallerMatrixTests.Host>
{
    public enum Who
    {
        None,
        BadJwt,
        ExpiredJwt,
        DisabledUser,
        MustChangeUser,
        Viewer,
        Editor,
        Admin,
        LegacyKey,
        N8nKey,
        BothHeaders,
    }

    public enum Expect
    {
        Passed,
        Unauthorized,
        Forbidden,
        PasswordChangeRequired,
        Ambiguous,
    }

    private const string V1 = "/api/v1";

    private static readonly (string Method, string Path)[] Routes =
    [
        ("POST", V1 + "/saved-requests"),
        ("GET", V1 + "/saved-requests"),
        ("PUT", V1 + "/saved-requests/1"),
        ("POST", V1 + "/saved-requests/1/post"),
        ("POST", V1 + "/saved-requests/1/resolve"),
        ("GET", V1 + "/users"),
        ("GET", V1 + "/quickbooks/lists"),
        ("POST", V1 + "/quickbooks/lists/sync"),
        ("POST", V1 + "/quickbooks/connection/test"),
        ("POST", V1 + "/quickbooks/transactions/validate-and-post"),
    ];

    private const Expect P = Expect.Passed;
    private const Expect F = Expect.Forbidden;

    /// <summary>One row per caller, one column per entry of <see cref="Routes"/>, in order.</summary>
    private static readonly Dictionary<Who, Expect[]> Table = new()
    {
        [Who.None] = Same(Expect.Unauthorized),
        [Who.BadJwt] = Same(Expect.Unauthorized),
        [Who.ExpiredJwt] = Same(Expect.Unauthorized),
        [Who.DisabledUser] = Same(Expect.Unauthorized),
        [Who.MustChangeUser] = Same(Expect.PasswordChangeRequired),
        //                submit read put post resolve users lists sync conn v&p
        [Who.Viewer] = [F, P, F, F, F, F, P, F, F, F],
        [Who.Editor] = [P, P, P, P, F, F, P, F, F, F],
        [Who.Admin] = [P, P, P, P, P, P, P, F, F, F],
        [Who.LegacyKey] = [P, P, F, F, F, F, P, P, P, P],
        [Who.N8nKey] = [P, F, F, F, F, F, F, F, F, F],
        [Who.BothHeaders] = Same(Expect.Ambiguous),
    };

    public static TheoryData<Who, string, string, Expect> Matrix()
    {
        var data = new TheoryData<Who, string, string, Expect>();
        foreach (var (who, row) in Table)
        {
            for (var i = 0; i < Routes.Length; i++)
            {
                data.Add(who, Routes[i].Method, Routes[i].Path, row[i]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Should_DecideAuthAsTheRoleTableSays_When_ACallerCallsARoute(Who who, string method, string path, Expect expected)
    {
        using var client = host.ClientFor(who);

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(expected, await OutcomeOf(response));
    }

    [Theory]
    [InlineData("GET", V1 + "/auth/me")]
    [InlineData("POST", V1 + "/auth/change-password")]
    [InlineData("POST", V1 + "/auth/logout")]
    public async Task Should_PassAuth_When_MustChangeUserCallsTheirOwnAccountRoutes(string method, string path)
    {
        using var client = host.ClientFor(Who.MustChangeUser);

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(Expect.Passed, await OutcomeOf(response));
    }

    [Fact]
    public async Task Should_RefuseApiKey_When_TheRouteNeedsASignedInPerson()
    {
        using var client = host.ClientFor(Who.LegacyKey);

        using var response = await client.GetAsync(V1 + "/auth/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(V1 + "/auth/login")]
    [InlineData(V1 + "/auth/refresh")]
    public async Task Should_NeedNoCredential_When_SigningIn(string path)
    {
        using var client = host.ClientFor(Who.None);

        using var response = await client.SendAsync(Request("POST", path));

        // T-1010: the routes now exist, and refresh without a cookie gives its own 401. What this asserts is that the
        // caller resolver let the request through, so that answer counts as passing and the resolver's do not.
        await response.Content.LoadIntoBufferAsync();
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode != HttpStatusCode.Unauthorized || body.Contains("invalid-refresh-token", StringComparison.Ordinal),
            $"{path} answered {(int)response.StatusCode} {body}");
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("ambiguous-credentials", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Who.Viewer)]
    [InlineData(Who.LegacyKey)]
    public async Task Should_ShowTheDatabaseLight_When_CallerHoldsRequestsReadOrHealthRead(Who who)
    {
        using var client = host.ClientFor(who);

        using var response = await client.GetAsync(V1 + "/health/database");

        Assert.Equal(Expect.Passed, await OutcomeOf(response));
    }

    [Fact]
    public async Task Should_RefuseTheDatabaseLight_When_KeyHoldsNeitherScope()
    {
        using var client = host.ClientFor(Who.N8nKey);

        using var response = await client.GetAsync(V1 + "/health/database");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_AuditTheUserAsUserId_When_ASignedInPersonChangesSomething()
    {
        var token = host.Factory.Users.TokenFor(DashboardRoles.Editor);
        using var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(Request("POST", V1 + "/saved-requests"));

        var expected = "user:" + host.Factory.Users.IdOf(token);
        Assert.Contains(host.AuditLines(), line => line.GetProperty("clientId").GetString() == expected);
    }

    /// <summary>The answer, read as far as the auth decision.</summary>
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

    private static Expect[] Same(Expect value) => Enumerable.Repeat(value, Routes.Length).ToArray();

    /// <summary>One host for the whole table; each user is new per request, so no row changes another.</summary>
    public sealed class Host : IDisposable
    {
        public ApiFactory Factory { get; } = new();

        private readonly string _n8nKey;

        public Host()
        {
            _n8nKey = ApiClientStore.NewKey();
            var secret = ApiClientStore.NewSecret(_n8nKey);
            var file = Factory.Dir.Combine("data", "clients.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            new ApiClientStore(file).Save(new ApiClientList
            {
                Clients =
                [
                    new ApiClient
                    {
                        Id = "n8n",
                        Name = "n8n workflow",
                        KeyHash = secret.KeyHash,
                        KeySalt = secret.KeySalt,
                        Scopes = [ApiScopes.RequestsSubmit],
                        CreatedUtc = DateTime.UtcNow.AddDays(-1),
                    },
                ],
            });
        }

        public HttpClient ClientFor(Who who)
        {
            var client = Factory.CreateClient();
            var users = Factory.Users;
            switch (who)
            {
                case Who.None:
                    break;
                case Who.BadJwt:
                    Bearer(client, users.ForgedTokenFor(DashboardRoles.Admin));
                    break;
                case Who.ExpiredJwt:
                    Bearer(client, users.ExpiredTokenFor(DashboardRoles.Admin));
                    break;
                case Who.DisabledUser:
                    Bearer(client, users.TokenFor(DashboardRoles.Admin, disabled: true));
                    break;
                case Who.MustChangeUser:
                    Bearer(client, users.TokenFor(DashboardRoles.Admin, mustChange: true));
                    break;
                case Who.Viewer:
                    Bearer(client, users.TokenFor(DashboardRoles.Viewer));
                    break;
                case Who.Editor:
                    Bearer(client, users.TokenFor(DashboardRoles.Editor));
                    break;
                case Who.Admin:
                    Bearer(client, users.TokenFor(DashboardRoles.Admin));
                    break;
                case Who.LegacyKey:
                    client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
                    break;
                case Who.N8nKey:
                    client.DefaultRequestHeaders.Add("X-Api-Key", _n8nKey);
                    break;
                case Who.BothHeaders:
                    Bearer(client, users.TokenFor(DashboardRoles.Admin));
                    client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
                    break;
            }

            return client;
        }

        public JsonElement[] AuditLines()
        {
            var dir = Factory.Dir.Combine("data", "logs");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, AuditLog.FilePrefix + "*" + AuditLog.FileExtension) : [];
            return files.SelectMany(File.ReadAllLines)
                .Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .ToArray();
        }

        public void Dispose() => Factory.Dispose();

        private static void Bearer(HttpClient client, string token) =>
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
