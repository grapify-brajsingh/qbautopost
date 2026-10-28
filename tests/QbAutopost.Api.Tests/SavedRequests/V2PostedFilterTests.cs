using System.Net;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using static QbAutopost.Api.Tests.SavedRequests.DashboardSeed;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212a (dashboard-api-m12 5, R-12): <c>postedFrom</c>/<c>postedTo</c>, <c>sort=posted</c>, <c>tz</c> and instant
/// bounds on <c>GET /api/v2/saved-requests</c>. Without <c>tz</c> a plain date keeps its UTC-day meaning.
/// </summary>
public sealed class V2PostedFilterTests : IDisposable
{
    private const string Route = "/api/v2/saved-requests";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private static DateTime Utc(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 10, day, hour, minute, second, DateTimeKind.Utc);

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string query)
    {
        using var client = _host.User(DashboardRoles.Viewer);
        using var response = await client.GetAsync(Route + "?pageSize=100&" + query);
        return (response.StatusCode, await SavedRequestTestHost.BodyOf(response));
    }

    private async Task<long[]> IdsAsync(string query)
    {
        var (status, body) = await GetAsync(query);
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToArray();
    }

    [Fact]
    public async Task Should_FilterPostedLocalDay_When_TzKolkata()
    {
        var before = await PostedAtAsync(Store, "B", Utc(26, 18, 29, 59));
        var first = await PostedAtAsync(Store, "F", Utc(26, 18, 30));
        var last = await PostedAtAsync(Store, "L", Utc(27, 18, 29, 59));
        var after = await PostedAtAsync(Store, "A", Utc(27, 18, 30));
        var unposted = await SaveAsync(Store, "U", Utc(27, 6));

        var ids = await IdsAsync("postedFrom=2026-10-27&postedTo=2026-10-27&tz=Asia/Kolkata");

        Assert.Equal(new[] { first, last }.Order(), ids.Order());
        Assert.DoesNotContain(before, ids);
        Assert.DoesNotContain(after, ids);
        Assert.DoesNotContain(unposted, ids);
    }

    [Fact]
    public async Task Should_FilterCreatedLocalDay_When_TzKolkata()
    {
        var before = await SaveAsync(Store, "B", Utc(26, 18, 29));
        var inside = await SaveAsync(Store, "I", Utc(26, 18, 30));
        var after = await SaveAsync(Store, "A", Utc(27, 18, 30));

        var ids = await IdsAsync("createdFrom=2026-10-27&createdTo=2026-10-27&tz=Asia/Kolkata");

        Assert.Equal(new[] { inside }, ids);
        Assert.DoesNotContain(before, ids);
        Assert.DoesNotContain(after, ids);
    }

    [Fact]
    public async Task Should_FilterReceivedLocalDay_When_TzKolkata()
    {
        var byEmail = await SaveAsync(Store, "E", Utc(20, 0), emailReceivedUtc: Utc(26, 19));
        var bySave = await SaveAsync(Store, "S", Utc(27, 18));
        await SaveAsync(Store, "X", Utc(26, 18), emailReceivedUtc: Utc(26, 18));

        var ids = await IdsAsync("receivedFrom=2026-10-27&receivedTo=2026-10-27&tz=Asia/Kolkata");

        Assert.Equal(new[] { byEmail, bySave }.Order(), ids.Order());
    }

    [Fact]
    public async Task Should_AcceptInstant_When_OffsetGiven()
    {
        var early = await PostedAtAsync(Store, "E", Utc(26, 18, 0));
        var inside = await PostedAtAsync(Store, "I", Utc(26, 18, 30));
        var atTo = await PostedAtAsync(Store, "T", Utc(27, 6, 0));

        // From inclusive, To exclusive; tz is ignored for a parameter that names its own offset.
        var ids = await IdsAsync("postedFrom=2026-10-27T00:00:00%2B05:30&postedTo=2026-10-27T06:00:00Z&tz=America/New_York");

        Assert.Equal(new[] { inside }, ids);
        Assert.DoesNotContain(early, ids);
        Assert.DoesNotContain(atTo, ids);
    }

    [Fact]
    public async Task Should_AcceptInstant_When_CreatedBoundsHaveOffsets()
    {
        await SaveAsync(Store, "B", Utc(27, 9, 59));
        var inside = await SaveAsync(Store, "I", Utc(27, 10, 0));
        await SaveAsync(Store, "A", Utc(27, 11, 0));

        var ids = await IdsAsync("createdFrom=2026-10-27T10:00:00Z&createdTo=2026-10-27T11:00:00Z");

        Assert.Equal(new[] { inside }, ids);
    }

    [Fact]
    public async Task Should_KeepUtcDates_When_NoTz()
    {
        await SaveAsync(Store, "B", Utc(26, 23, 30));
        var created = await SaveAsync(Store, "C", Utc(27, 0, 30));
        await SaveAsync(Store, "A", Utc(28, 0, 0));
        var posted = await PostedAtAsync(Store, "P", Utc(27, 23, 59));
        await PostedAtAsync(Store, "Q", Utc(28, 0, 0));

        Assert.Equal(new[] { created }, await IdsAsync("createdFrom=2026-10-27&createdTo=2026-10-27"));
        Assert.Equal(new[] { posted }, await IdsAsync("postedFrom=2026-10-27&postedTo=2026-10-27"));
    }

    [Theory]
    [InlineData("posted")]
    [InlineData("-posted")]
    public async Task Should_SortUnpostedLast_When_SortPosted(string sort)
    {
        var unposted = await SaveAsync(Store, "U", Utc(27, 6));
        var early = await PostedAtAsync(Store, "E", Utc(26, 1));
        var late = await PostedAtAsync(Store, "L", Utc(27, 1));

        var ids = await IdsAsync("sort=" + sort);

        long[] expected = sort == "posted" ? [early, late, unposted] : [late, early, unposted];
        Assert.Equal(expected, ids);
    }

    [Theory]
    [InlineData("tz=Mars/Olympus&postedFrom=2026-10-27")]
    [InlineData("tz=Mars/Olympus")]
    [InlineData("postedFrom=27-10-2026")]
    [InlineData("postedTo=2026-10-27T10:00:00")]
    [InlineData("createdFrom=2026-10-27T10:00")]
    [InlineData("receivedTo=yesterday")]
    public async Task Should_Return400_When_TzOrDateBad(string query)
    {
        var (status, body) = await GetAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
    }

    [Fact]
    public void Should_DocumentParameters_When_OpenApiV2Read()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "openapi-v2.json")));
        var get = document.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("get");
        var parameters = get.GetProperty("parameters").EnumerateArray()
            .Where(p => p.TryGetProperty("name", out _))
            .ToDictionary(p => p.GetProperty("name").GetString()!);

        Assert.Contains("postedFrom", parameters.Keys);
        Assert.Contains("postedTo", parameters.Keys);
        Assert.Contains("tz", parameters.Keys);
        var sorts = parameters["sort"].GetProperty("schema").GetProperty("enum").EnumerateArray().Select(s => s.GetString());
        Assert.Contains("posted", sorts);
        Assert.Contains("-posted", sorts);
    }
}
