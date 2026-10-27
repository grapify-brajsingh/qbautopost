using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Models;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-914 (api-v1 §3): the two list routes the v1 table names — <c>GET /api/v1/quickbooks/lists</c>, which reads the
/// cached <c>qb-lists.json</c> so a caller can pick a valid account or vendor without guessing, and
/// <c>POST /api/v1/quickbooks/lists/sync</c>, the versioned spelling of <c>POST /qb/sync-lists</c>.
/// <para>
/// The read never reaches QuickBooks: that is the whole point of the cache, and a route a client polls to fill a
/// drop-down must not be able to queue behind a post.
/// </para>
/// </summary>
public sealed class QbListsApiTests : IDisposable
{
    private const string ListsRoute = ApiRoutes.V1Prefix + "/quickbooks/lists";
    private const string SyncRoute = ListsRoute + "/sync";

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public QbListsApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_AnswerWithEmptyLists_When_NothingHasBeenSyncedYet()
    {
        using var response = await _client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("accounts").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("syncedUtc").ValueKind);
    }

    [Fact]
    public async Task Should_AnswerWithTheCachedNames_When_TheListsHaveBeenSynced()
    {
        Seed();

        using var response = await _client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["Chase Checking 4521", "Utilities"],
            body.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Home Depot"], body.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>The cache exists so a caller can read names while QuickBooks is shut down, or busy with a post.</summary>
    [Fact]
    public async Task Should_NeverCallQuickBooks_When_TheListsAreRead()
    {
        Seed();

        using var response = await _client.GetAsync(ListsRoute);

        response.EnsureSuccessStatusCode();
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Return401_When_TheListsAreReadWithoutAKey()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return403_When_TheKeyLacksQbRead()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", GiveClientAKey(ApiScopes.JobsRead));

        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_SyncTheLists_When_TheVersionedSyncRouteIsCalled()
    {
        _factory.Gateway.Company.Accounts.Add(new QbAccount { Name = "Chase Checking 4521", Type = "Bank" });

        using var response = await _client.PostAsync(SyncRoute, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("accounts").GetInt32());
    }

    /// <summary>
    /// api-v1 §9: the flat path of spec §6 keeps working while <c>Api:LegacyRoutes</c> is true, so the POC package
    /// and any caller written before M9 are not broken by the rename.
    /// </summary>
    [Fact]
    public async Task Should_StillAnswerTheFlatSyncPath_When_LegacyRoutesAreOn()
    {
        using var response = await _client.PostAsync("/qb/sync-lists", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Issues one caller with exactly the scopes named, and returns their key.</summary>
    private string GiveClientAKey(params string[] scopes)
    {
        var file = _factory.Dir.Combine("data", "clients.json");
        var key = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        new ApiClientStore(file).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "list-reader",
                    Name = "List reader",
                    KeyHash = secret.KeyHash,
                    KeySalt = secret.KeySalt,
                    Scopes = scopes,
                    CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });

        return key;
    }

    private void Seed()
    {
        var file = _factory.Dir.Combine("data", "qb-lists.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        new Core.Store.QbListsStore(file).Save(new QbLists
        {
            SyncedUtc = new DateTime(2026, 9, 20, 14, 2, 0, DateTimeKind.Utc),
            Accounts =
            [
                new QbAccount { Name = "Chase Checking 4521", Type = "Bank" },
                new QbAccount { Name = "Utilities", Type = "Expense" },
            ],
            Vendors = ["Home Depot"],
            Customers = ["Palm Court Rentals LLC"],
        });
    }
}
