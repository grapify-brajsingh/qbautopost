using System.Net;
using System.Text.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Core.Security;
using static QbAutopost.Api.Tests.SavedRequests.DashboardSeed;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>T-1212a (dashboard-api-m12 4): <c>GET /api/v2/dashboard/summary</c> over the fake store.</summary>
public sealed class V2SummaryTests : IDisposable
{
    private const string Route = "/api/v2/dashboard/summary";

    // Kolkata days 2026-10-26, 27 and 28; the compare range is 2026-10-20 to 22 local.
    private const string Range = "from=2026-10-26T00:00:00%2B05:30&to=2026-10-29T00:00:00%2B05:30&tz=Asia/Kolkata";
    private const string Compare = "compareFrom=2026-10-20T00:00:00%2B05:30&compareTo=2026-10-23T00:00:00%2B05:30";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private static DateTime Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>
    /// In range: received 1 (2026-10-27T20:00Z, local 10-28), posted 1 (10-27T06:00Z, 100.25 + 60), post-failed 1
    /// (10-26T10:00Z), discarded 1 (10-28T10:00Z). In the compare range: received 2, posted 1 (60.00).
    /// </summary>
    private async Task SeedAsync()
    {
        await SaveAsync(Store, "R1", Utc(10, 27, 20));
        await PostedAtAsync(Store, "P1", Utc(10, 27, 6), 100.25m, 60m);
        var failed = await SaveAsync(Store, "F1", Utc(10, 21, 0));
        await AttemptAsync(Store, failed, AttemptOutcome.Failed, SavedRequestStatus.Pending, Utc(10, 26, 10));
        var discarded = await SaveAsync(Store, "D1", Utc(10, 21, 0));
        await DiscardAsync(Store, discarded, Utc(10, 28, 10));
        await PostedAtAsync(Store, "P0", Utc(10, 21, 12), 60.00m);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string query, string role = DashboardRoles.Viewer)
    {
        using var client = _host.User(role);
        using var response = await client.GetAsync(Route + "?" + query);
        return (response.StatusCode, await SavedRequestTestHost.BodyOf(response));
    }

    [Fact]
    public async Task Should_ReturnSummaryShape_When_RangeGiven()
    {
        await SeedAsync();

        var (status, body) = await GetAsync(Range + "&" + Compare);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            new[] { "range", "counts", "previous", "posted", "timeToPost", "series" },
            body.EnumerateObject().Select(p => p.Name));

        var range = body.GetProperty("range");
        Assert.Equal("2026-10-26T00:00:00+05:30", range.GetProperty("from").GetString());
        Assert.Equal("2026-10-29T00:00:00+05:30", range.GetProperty("to").GetString());
        Assert.Equal("Asia/Kolkata", range.GetProperty("tz").GetString());
        Assert.Equal("day", range.GetProperty("bucket").GetString());

        Assert.Equal("""{"received":1,"posted":1,"postFailed":1,"discarded":1}""", body.GetProperty("counts").GetRawText());
        Assert.Equal("""{"received":2,"posted":1,"postFailed":0,"discarded":0}""", body.GetProperty("previous").GetRawText());

        var posted = body.GetProperty("posted");
        Assert.Equal(2, posted.GetProperty("rows").GetInt32());
        Assert.Equal("160.25", posted.GetProperty("amount").GetRawText());
        Assert.Equal(1, posted.GetProperty("previousRows").GetInt32());
        Assert.Equal("60.00", posted.GetProperty("previousAmount").GetRawText());

        Assert.Equal("""{"medianMinutes":4320,"p90Minutes":4320,"statements":1}""", body.GetProperty("timeToPost").GetRawText());

        Assert.Equal(
            """[{"start":"2026-10-26","received":0,"posted":0,"postFailed":1},{"start":"2026-10-27","received":0,"posted":1,"postFailed":0},{"start":"2026-10-28","received":1,"posted":0,"postFailed":0}]""",
            body.GetProperty("series").GetRawText());
    }

    [Fact]
    public async Task Should_WriteTwoDecimals_When_AmountIsWhole()
    {
        await PostedAtAsync(Store, "W", Utc(10, 27, 6), 60m);

        var (_, body) = await GetAsync(Range);

        Assert.Equal("60.00", body.GetProperty("posted").GetProperty("amount").GetRawText());
    }

    [Fact]
    public async Task Should_WriteNegativeWithTwoDecimals_When_AmountNegative()
    {
        await PostedAtAsync(Store, "N", Utc(10, 27, 6), -12.5m);

        var (_, body) = await GetAsync(Range);

        Assert.Equal("-12.50", body.GetProperty("posted").GetProperty("amount").GetRawText());
    }

    [Fact]
    public async Task Should_WriteEveryDecimal_When_StoredAmountHasMoreThanTwo()
    {
        // Never rounded (rule 3): stored amounts have 2 decimals, so 2 are written; a third is kept, not dropped.
        await PostedAtAsync(Store, "X", Utc(10, 27, 6), 10.125m);

        var (_, body) = await GetAsync(Range);

        Assert.Equal("10.125", body.GetProperty("posted").GetProperty("amount").GetRawText());
    }

    [Fact]
    public async Task Should_ReturnNullPreviousAndTimeToPost_When_NoCompareAndNoPosts()
    {
        await SaveAsync(Store, "R1", Utc(10, 27, 20));

        var (status, body) = await GetAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("previous").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("timeToPost").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("posted").GetProperty("previousRows").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("posted").GetProperty("previousAmount").ValueKind);
        Assert.Equal("0.00", body.GetProperty("posted").GetProperty("amount").GetRawText());
        var range = body.GetProperty("range");
        Assert.Equal(
            new[] { "2026-10-27T00:00:00+00:00", "2026-10-28T00:00:00+00:00", "UTC", "day" },
            new[] { "from", "to", "tz", "bucket" }.Select(k => range.GetProperty(k).GetString()));
    }

    [Fact]
    public async Task Should_BucketByUtcDay_When_NoTz()
    {
        await SaveAsync(Store, "R1", Utc(10, 27, 20));

        var (_, body) = await GetAsync("from=2026-10-27T00:00:00Z&to=2026-10-29T00:00:00Z");

        Assert.Equal("2026-10-27", body.GetProperty("series")[0].GetProperty("start").GetString());
        Assert.Equal(1, body.GetProperty("series")[0].GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task Should_GiveOneBucketPerLocalDay_When_RangeCrossesDst()
    {
        var (status, body) = await GetAsync(
            "from=2026-10-31T00:00:00-04:00&to=2026-11-03T00:00:00-05:00&tz=America/New_York");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            new[] { "2026-10-31", "2026-11-01", "2026-11-02" },
            body.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("start").GetString()));
        Assert.Equal("2026-10-31T00:00:00-04:00", body.GetProperty("range").GetProperty("from").GetString());
        Assert.Equal("2026-11-03T00:00:00-05:00", body.GetProperty("range").GetProperty("to").GetString());
    }

    [Fact]
    public async Task Should_StartWeeksOnMonday_When_BucketWeek()
    {
        var (status, body) = await GetAsync("from=2026-10-28T00:00:00Z&to=2026-11-04T00:00:00Z&bucket=week");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("week", body.GetProperty("range").GetProperty("bucket").GetString());
        Assert.Equal(
            new[] { "2026-10-26", "2026-11-02" },
            body.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("start").GetString()));
    }

    [Fact]
    public async Task Should_EqualAuditTotals_When_SameRange()
    {
        await SeedAsync();
        const string from = "2026-10-26T00:00:00%2B05:30";
        const string to = "2026-10-29T00:00:00%2B05:30";

        var (_, body) = await GetAsync(Range);

        var counts = body.GetProperty("counts");
        using var viewer = _host.User(DashboardRoles.Viewer);
        foreach (var (action, key) in new[]
                 {
                     (AuditActions.Received, "received"), (AuditActions.Posted, "posted"),
                     (AuditActions.PostFailed, "postFailed"), (AuditActions.Discarded, "discarded"),
                 })
        {
            using var response = await viewer.GetAsync($"/api/v2/audit-events?action={action}&from={from}&to={to}&pageSize=1");
            var total = (await SavedRequestTestHost.BodyOf(response)).GetProperty("total").GetInt32();
            Assert.Equal(total, counts.GetProperty(key).GetInt32());
        }

        var series = body.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(counts.GetProperty("received").GetInt32(), series.Sum(s => s.GetProperty("received").GetInt32()));
        Assert.Equal(counts.GetProperty("posted").GetInt32(), series.Sum(s => s.GetProperty("posted").GetInt32()));
        Assert.Equal(counts.GetProperty("postFailed").GetInt32(), series.Sum(s => s.GetProperty("postFailed").GetInt32()));
    }

    [Theory]
    [InlineData("from=2026-10-27T00:00:00Z&to=2026-10-27T00:00:00Z")]
    [InlineData("from=2026-10-28T00:00:00Z&to=2026-10-27T00:00:00Z")]
    public async Task Should_Return400_When_ToNotAfterFrom(string query) => await AssertInvalidAsync(query);

    [Theory]
    [InlineData("from=2025-10-27T00:00:00Z&to=2026-10-28T00:00:01Z")]
    [InlineData("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&compareFrom=2025-01-01T00:00:00Z&compareTo=2026-01-03T00:00:00Z")]
    public async Task Should_Return400_When_RangeOver366Days(string query) => await AssertInvalidAsync(query);

    [Fact]
    public async Task Should_Accept366DayRange_When_ExactlyAtLimit()
    {
        var (status, _) = await GetAsync("from=2025-10-27T00:00:00Z&to=2026-10-28T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Should_Return400_When_OnlyCompareFromSent() =>
        await AssertInvalidAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&compareFrom=2026-10-20T00:00:00Z");

    [Fact]
    public async Task Should_Return400_When_OnlyCompareToSent() =>
        await AssertInvalidAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&compareTo=2026-10-20T00:00:00Z");

    [Fact]
    public async Task Should_Return400_When_CompareToNotAfterCompareFrom() =>
        await AssertInvalidAsync(
            "from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&compareFrom=2026-10-20T00:00:00Z&compareTo=2026-10-20T00:00:00Z");

    [Fact]
    public async Task Should_Return400_When_TzUnknown() =>
        await AssertInvalidAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&tz=Mars/Olympus");

    [Theory]
    [InlineData("tz=")]
    [InlineData("tz=%20")]
    [InlineData("tz=%20Asia/Kolkata")]
    [InlineData("tz=Asia/Kolkata%20")]
    public async Task Should_Return400_When_TzBlankOrPadded(string tz) =>
        await AssertInvalidAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&" + tz);

    [Theory]
    [InlineData("from=2026-10-27T00:00:00Z%0A&to=2026-10-28T00:00:00Z")]
    [InlineData("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z%0A")]
    public async Task Should_Return400_When_InstantHasTrailingNewline(string query) => await AssertInvalidAsync(query);

    [Theory]
    [InlineData("bucket=month")]
    [InlineData("bucket=Day")]
    public async Task Should_Return400_When_BucketUnknown(string bucket) =>
        await AssertInvalidAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z&" + bucket);

    [Theory]
    [InlineData("to=2026-10-28T00:00:00Z")]
    [InlineData("from=2026-10-27T00:00:00Z")]
    [InlineData("from=2026-10-27&to=2026-10-28")]
    [InlineData("from=2026-10-27T00:00:00&to=2026-10-28T00:00:00")]
    [InlineData("from=yesterday&to=2026-10-28T00:00:00Z")]
    [InlineData("from=2026-10-27T00:00:00Z&from=2026-10-26T00:00:00Z&to=2026-10-28T00:00:00Z")]
    public async Task Should_Return400_When_RangeMissingOrNotAnInstant(string query) => await AssertInvalidAsync(query);

    [Theory]
    [InlineData(DashboardRoles.Admin)]
    [InlineData(DashboardRoles.Editor)]
    [InlineData(DashboardRoles.Viewer)]
    public async Task Should_AllowEveryRole_When_RequestsRead(string role)
    {
        var (status, _) = await GetAsync("from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z", role);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ApiScopes.RequestsRead, ApiRoutes.ScopeFor("GET", Route));
    }

    [Theory]
    [InlineData("/api/v1/dashboard/summary")]
    [InlineData("/API/V1/Dashboard/Summary")]
    [InlineData("/dashboard/summary")]
    public void Should_KeepAdminScope_When_SummaryPathIsNotV2(string path) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor("GET", path));

    [Fact]
    public async Task Should_Refuse403_When_ViewerCallsV1SummaryPath()
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync("/api/v1/dashboard/summary?from=2026-10-27T00:00:00Z&to=2026-10-28T00:00:00Z");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void Should_CloseToAdmin_When_SummaryCalledWithOtherMethod(string method) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor(method, Route));

    [Fact]
    public void Should_DocumentRoute_When_OpenApiV2Read()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "openapi-v2.json")));

        var get = document.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("get");

        Assert.Equal(ApiScopes.RequestsRead, get.GetProperty("x-required-scope").GetString());
        Assert.Equal(
            new[] { "from", "to", "compareFrom", "compareTo", "bucket", "tz" },
            get.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.True(get.GetProperty("responses").TryGetProperty("400", out _));
    }

    private async Task AssertInvalidAsync(string query)
    {
        var (status, body) = await GetAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
    }
}
