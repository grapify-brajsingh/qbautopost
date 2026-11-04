using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Core.Security;
using static QbAutopost.Api.Tests.SavedRequests.DashboardSeed;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1713 (M17 spec 5.3, 6 f, ruling B20): the <c>company</c> filter of <c>GET /api/v2/dashboard/summary</c> and
/// <c>GET /api/v2/audit-events</c>, and <c>companyId</c> on the events. Absent is every company.
/// </summary>
public sealed class V2CompanyFilterTests : IDisposable
{
    private const string Summary = "/api/v2/dashboard/summary";
    private const string Feed = "/api/v2/audit-events";
    private const string Csv = "/api/v2/audit-events.csv";
    private const string Range = "from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:00Z";
    private const int Takoma = 2;
    private const int Sa = 3;

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private static DateTime Utc(int day, int hour) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);

    /// <summary>Takoma: one received, one posted (10.00). S &amp; A: two received. One request with no company.</summary>
    private async Task SeedAsync()
    {
        var posted = await SaveAsync(Store, "T1", Utc(10, 1), companyId: Takoma);
        await AttemptAsync(Store, posted, AttemptOutcome.Posted, SavedRequestStatus.Posted, Utc(10, 3), 10m);
        await SaveAsync(Store, "S1", Utc(11, 1), companyId: Sa);
        await SaveAsync(Store, "S2", Utc(12, 1), companyId: Sa);
        await SaveAsync(Store, "N1", Utc(13, 1));
        Store.AddUserAudit(new UserAuditEntry(
            0, null, "system", "companies-scanned", JsonNode.Parse($$"""{"companyId":{{Takoma}}}"""), Utc(14, 1)));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, string role = DashboardRoles.Viewer)
    {
        using var client = _host.User(role);
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await SavedRequestTestHost.BodyOf(response));
    }

    private static int Received(JsonElement summary) => summary.GetProperty("counts").GetProperty("received").GetInt32();

    [Fact]
    public async Task Should_FilterSummaryByCompany_When_CompanyParameterSent()
    {
        await SeedAsync();

        var (status, takoma) = await GetAsync($"{Summary}?{Range}&company={Takoma}");
        var (_, sa) = await GetAsync($"{Summary}?{Range}&company={Sa}");
        var (_, unknown) = await GetAsync($"{Summary}?{Range}&company=99");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((1, 1), (Received(takoma), takoma.GetProperty("counts").GetProperty("posted").GetInt32()));
        Assert.Equal(10.00m, takoma.GetProperty("posted").GetProperty("amount").GetDecimal());
        Assert.Equal((2, 0), (Received(sa), sa.GetProperty("counts").GetProperty("posted").GetInt32()));
        Assert.Equal(0, Received(unknown));
    }

    [Fact]
    public async Task Should_FilterAuditFeedByCompany_When_CompanyParameterSent()
    {
        await SeedAsync();

        var (status, body) = await GetAsync($"{Feed}?company={Sa}");
        var (_, unknown) = await GetAsync($"{Feed}?company=99");

        Assert.Equal(HttpStatusCode.OK, status);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, e => Assert.Equal(Sa, e.GetProperty("companyId").GetInt32()));
        Assert.Equal(0, unknown.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Should_ShowCompanyIdOnEvents_When_NoCompanyParameterSentToTheFeed()
    {
        await SeedAsync();

        var (_, body) = await GetAsync(Feed);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        var received = items.Where(e => e.GetProperty("action").GetString() == "received").ToList();
        Assert.Equal(1, received.Count(e => e.GetProperty("companyId").ValueKind == JsonValueKind.Null));
        Assert.Equal(
            [Takoma, Sa, Sa],
            received.Where(e => e.GetProperty("companyId").ValueKind == JsonValueKind.Number)
                .Select(e => e.GetProperty("companyId").GetInt32()).Order().ToList());
        Assert.Contains(items, e => e.GetProperty("action").GetString() == "companies-scanned"
            && e.GetProperty("companyId").GetInt32() == Takoma);
    }

    [Fact]
    public async Task Should_AnswerAsBefore_When_NoCompanyParameter()
    {
        await SeedAsync();

        var (_, summary) = await GetAsync($"{Summary}?{Range}");
        var (_, feed) = await GetAsync(Feed);

        Assert.Equal(4, Received(summary));
        // 4 received + post-started + posted + companies-scanned: every company's events, none dropped.
        Assert.Equal(7, feed.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("x")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1&company=2")]
    public async Task Should_Answer400_When_CompanyParameterIsNotAnId(string value)
    {
        foreach (var path in new[] { $"{Summary}?{Range}&company={value}", $"{Feed}?company={value}", $"{Csv}?company={value}" })
        {
            var (status, body) = await GetAsync(path);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("invalid-request", body.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Should_FilterCsvByCompany_When_CompanyParameterSent()
    {
        await SeedAsync();

        using var client = _host.User(DashboardRoles.Viewer);
        using var response = await client.GetAsync($"{Csv}?company={Sa}");
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1 + 2, lines.Length);
    }
}
