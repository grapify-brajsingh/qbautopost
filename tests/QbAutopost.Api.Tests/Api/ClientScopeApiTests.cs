using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-910 / FR-A-13: per-caller keys and scopes. With remote callers (D-4), "who asked for this posting, and were
/// they allowed to?" must have an answer that is not "whoever had the one key".
/// <para>
/// The shape of the guarantee: an unknown key is <c>401</c>, a known key without the scope is <c>403</c>, and the
/// shared key keeps working as an implicit all-scopes client only while <c>Api:AllowLegacyKey</c> is true.
/// </para>
/// </summary>
public sealed class ClientScopeApiTests : IDisposable
{
    private const string PostRoute = "/api/v1/quickbooks/transactions";
    private const string ValidateRoute = "/api/v1/quickbooks/transactions/validate";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string ClientsFile => _factory.Dir.Combine("data", "clients.json");

    /// <summary>Writes <c>clients.json</c> with one caller and returns the key they were issued.</summary>
    private string GiveClientAKey(params string[] scopes)
    {
        var key = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(key);
        Directory.CreateDirectory(Path.GetDirectoryName(ClientsFile)!);
        new ApiClientStore(ClientsFile).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "acme-erp",
                    Name = "Acme ERP",
                    KeyHash = secret.KeyHash,
                    KeySalt = secret.KeySalt,
                    Scopes = scopes,
                    CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });

        return key;
    }

    private static object Request() => new
    {
        reference = "payroll-2026-09",
        dryRun = true,
        controlTotal = 184.32m,
        transactions = new[]
        {
            new
            {
                externalId = "row-1",
                kind = "Check",
                date = "2026-08-05",
                amount = 184.32m,
                account = "Chase Checking 4521",
                payee = "HOME DEPOT #6412",
                memo = "HOME DEPOT #6412 PURCHASE",
                last4 = "4521",
            },
        },
    };

    private static HttpClient WithKey(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task Should_Answer200_When_TheClientHasTheScope()
    {
        var key = GiveClientAKey(ApiScopes.QbRead, ApiScopes.QbPost);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer403_When_TheClientLacksTheScope()
    {
        // A read-only integration must not be able to post, even though its key is perfectly valid.
        var key = GiveClientAKey(ApiScopes.QbRead);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains(ApiScopes.QbPost, problem.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Writes);
    }

    [Fact]
    public async Task Should_Answer200_When_TheSameClientOnlyValidates()
    {
        var key = GiveClientAKey(ApiScopes.QbRead);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(ValidateRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer401_When_TheKeyIsNotAClients()
    {
        GiveClientAKey(ApiScopes.QbPost);
        using var host = _factory.WithSetting("Api:AllowLegacyKey", "false");
        using var client = WithKey(host, "not-a-real-key");

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer403_When_TheClientCallsFromAnAddressOutsideItsList()
    {
        var key = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(key);
        Directory.CreateDirectory(Path.GetDirectoryName(ClientsFile)!);
        new ApiClientStore(ClientsFile).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "acme-erp", KeyHash = secret.KeyHash, KeySalt = secret.KeySalt,
                    Scopes = [ApiScopes.Admin], AllowedCidrs = ["203.0.113.0/24"],
                },
            ],
        });
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        // The test host has no remote address, which cannot be shown to be inside the list — so it is outside it.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer401_When_TheClientIsDisabled()
    {
        // A second, still-enabled caller, so this is a revocation and not "nobody can authenticate" — which the
        // startup check refuses outright, and rightly so.
        var key = GiveClientAKey(ApiScopes.Admin);
        var store = new ApiClientStore(ClientsFile);
        var revoked = store.Load().Clients[0] with { Enabled = false };
        var other = ApiClientStore.NewSecret(ApiClientStore.NewKey());
        store.Save(new ApiClientList
        {
            Clients =
            [
                revoked,
                new ApiClient
                {
                    Id = "other-caller", KeyHash = other.KeyHash, KeySalt = other.KeySalt, Scopes = [ApiScopes.QbRead],
                },
            ],
        });
        using var host = _factory.WithSetting("Api:AllowLegacyKey", "false");
        using var client = WithKey(host, key);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        // Revoking a caller is switching one flag, and it takes effect on the next request.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_StillAcceptTheSharedKey_When_LegacyKeysAreAllowed()
    {
        // Back-compat (FR-A-13): the one key keeps working as an implicit all-scopes client for one release.
        GiveClientAKey(ApiScopes.QbRead);
        using var client = WithKey(_factory, ApiFactory.ApiKey);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseTheSharedKey_When_LegacyKeysAreTurnedOff()
    {
        GiveClientAKey(ApiScopes.QbPost);
        using var host = _factory.WithSetting("Api:AllowLegacyKey", "false");
        using var client = WithKey(host, ApiFactory.ApiKey);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_NotAskForAKey_When_TheRouteIsLiveness()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_NeverLeakTheKey_When_ACallerIsRefused()
    {
        // CLAUDE.md rule 6: the answer names the client and the scope, never the secret.
        var key = GiveClientAKey(ApiScopes.QbRead);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.DoesNotContain(key, problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_WithholdTheQbXml_When_TheClientLacksTheDebugScope()
    {
        var key = GiveClientAKey(ApiScopes.QbRead);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(
            ValidateRoute + "?includeQbXml=true", Request(), ApiFactory.Json);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.False(body.GetProperty("qbXml").TryGetProperty("request", out _));
    }

    [Fact]
    public async Task Should_ReturnTheQbXml_When_TheClientHoldsTheDebugScope()
    {
        // T-907 left this gated behind a scope that did not exist yet; it exists now, so the gap closes.
        var key = GiveClientAKey(ApiScopes.QbRead, ApiScopes.QbDebug);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(
            ValidateRoute + "?includeQbXml=true", Request(), ApiFactory.Json);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains("CheckAddRq", body.GetProperty("qbXml").GetProperty("request").GetString());
    }
}
