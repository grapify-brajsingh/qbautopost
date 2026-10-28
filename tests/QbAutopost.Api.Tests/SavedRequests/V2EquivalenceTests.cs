using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1113 (dashboard v2 spec 6.1, O-1): with no row marked <c>post: false</c>, posting a v2 saved request by id gives the
/// same status code, the same body (minus the fields the saved route adds) and the same QuickBooks traffic as
/// validate-and-post. The comparison is made against the stored version 1, because a v2 save gives every row without
/// one an <c>externalId</c> (spec 7.1), and that is the payload the post sends. Each side runs in its own fresh host.
/// Unbindable payloads and payloads with a row off are excluded: v2 answers those itself.
/// </summary>
public sealed class V2EquivalenceTests
{
    private const string SyncPostRoute = "/api/v1/quickbooks/transactions/validate-and-post";
    private const string V2Route = "/api/v2/saved-requests";

    /// <summary>Fields only the saved route has: removed before the bodies are compared.</summary>
    private static readonly string[] Added = ["savedRequestId", "attemptId", "savedRequestStatus"];

    /// <summary>Top-level fields that differ between any two posts or requests: compared as present, not by value.</summary>
    private static readonly string[] PerCall = ["postId", "startedUtc", "finishedUtc", "requestId", "traceId"];

    /// <summary>Every fixture in <c>tests/fixtures/sync-post</c>, plus the gateway endings the fixtures alone cannot reach.</summary>
    public static TheoryData<string> Scenarios
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(Fixtures.PathOf("sync-post"), "*.json").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            data.Add("unavailable");
            data.Add("busy-timeout");
            data.Add("amount-mismatch");
            return data;
        }
    }

    private static (string Fixture, Action<FakeQbGateway> Arrange) Scenario(string name) => name switch
    {
        "partial" => (SyncPostFixtures.Partial, g => g.RejectLine = 2),
        "failed" => (SyncPostFixtures.Failed, g => g.RejectLine = 1),
        "no-response" => (
            SyncPostFixtures.NoResponse,
            g => g.Throw = new QuickBooksCallException("ProcessRequest failed (0x80010105)", unchecked((int)0x80010105))),
        "unavailable" => (SyncPostFixtures.PostedOneRow, g => g.Throw = new QuickBooksUnavailableException("simulated")),
        "busy-timeout" => (SyncPostFixtures.PostedOneRow, g => g.Throw = new QuickBooksBusyException("simulated")),
        "amount-mismatch" => (SyncPostFixtures.PostedOneRow, g => g.MisstateAmountLine = 1),
        _ => (name + ".json", _ => { }),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Should_MatchValidateAndPost_When_NoRowIsOff(string scenario)
    {
        var (fixture, arrange) = Scenario(scenario);

        using var saved = new SavedRequestTestHost();
        arrange(saved.Factory.Gateway);
        var id = await SaveV2Async(saved, SyncPostFixtures.Read(fixture));
        var version1 = (await saved.Factory.Store.GetAsync(id, CancellationToken.None))!.CurrentPayload;
        Assert.Empty(NotPostedRows.Read(version1).Indexes);
        Assert.Empty(NotPostedRows.Read(version1).Errors);
        Assert.True(SavedPayloadReader.Read(version1).Readable, "unbindable payloads are out of scope");
        using var editor = saved.User(DashboardRoles.Editor);
        using var actual = await editor.PostAsync($"{V2Route}/{id}/post", null);
        var actualBody = await BodyOf(actual);

        using var direct = new ApiFactory();
        arrange(direct.Gateway);
        using var directClient = direct.CreateAuthorizedClient();
        using var expected = await directClient.PostAsync(
            SyncPostRoute, new StringContent(version1.GetRawText(), Encoding.UTF8, "application/json"));
        var expectedBody = await BodyOf(expected);

        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.Content.Headers.ContentType?.MediaType, actual.Content.Headers.ContentType?.MediaType);
        foreach (var field in Added)
        {
            Assert.True(actualBody.ContainsKey(field), $"the saved route must add {field}");
            actualBody.Remove(field);
        }

        Normalise(expectedBody);
        Normalise(actualBody);
        Assert.True(
            JsonNode.DeepEquals(expectedBody, actualBody),
            $"bodies differ:\nvalidate-and-post: {expectedBody.ToJsonString()}\nv2 post by id:     {actualBody.ToJsonString()}");
        Assert.Equal(direct.Gateway.Requests, saved.Factory.Gateway.Requests);
    }

    private static async Task<long> SaveV2Async(SavedRequestTestHost host, string body)
    {
        using var client = host.N8n();
        using var response = await client.PostAsync(V2Route, SavedRequestTestHost.Json(body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
    }

    private static async Task<JsonObject> BodyOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!.AsObject();

    /// <summary>Each top-level per-call field becomes the marker <c>"*"</c> (a null stays null).</summary>
    private static void Normalise(JsonObject body)
    {
        foreach (var key in PerCall)
        {
            if (body[key] is not null)
            {
                body[key] = "*";
            }
        }
    }
}
