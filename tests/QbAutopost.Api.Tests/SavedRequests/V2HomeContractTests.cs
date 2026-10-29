using System.Net;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1204 (dashboard-api-m12 3.1 item 6): the Home page's queue is the oldest first by <c>received</c>, which falls back
/// to <c>created</c> when the request has no email time. The audit-feed points are pinned in the store contract tests.
/// </summary>
public sealed class V2HomeContractTests : IDisposable
{
    private const string V2Route = "/api/v2/saved-requests";
    private const string QueueQuery = "?status=pending&status=partial&status=unknown&sort=received&pageSize=100";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<long> SaveAsync(string reference)
    {
        var body = JsonNode.Parse(File.ReadAllText(Fixtures.PathOf("saved-requests-v2", "post-false.json")))!;
        body["reference"] = reference;
        using var client = _host.N8n();
        using var response = await client.PostAsync(V2Route, SavedRequestTestHost.Json(body.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Should_ReturnOldestFirst_When_QueueListSortedByReceived()
    {
        var store = _host.Factory.Store;
        var noEmail = await SaveAsync("home-no-email");
        var withEmail = await SaveAsync("home-with-email");
        var middle = await SaveAsync("home-middle");
        var discarded = await SaveAsync("home-discarded");

        // Received time is the email's when there is one, else the creation time.
        store.Change(noEmail, r => r with { CreatedUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc) });
        store.Change(withEmail, r => r with
        {
            CreatedUtc = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc),
            Metadata = r.Metadata with { EmailReceivedUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc) },
        });
        store.Change(middle, r => r with { CreatedUtc = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc) });
        store.Change(discarded, r => r with
        {
            Status = QbAutopost.Core.SavedRequests.SavedRequestStatus.Discarded,
            CreatedUtc = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc),
        });

        using var viewer = _host.User(DashboardRoles.Viewer);
        using var response = await viewer.GetAsync(V2Route + QueueQuery);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await SavedRequestTestHost.BodyOf(response)).GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(new[] { withEmail, middle, noEmail }, ids);
    }
}
