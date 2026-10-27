using System.Net.Http.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1014, SR-4 (saved-requests spec 7.2, 14.4), the test that matters most: posting a saved request by id gives the
/// same status code, the same body (minus the fields the saved route adds) and the same QuickBooks traffic as
/// validate-and-post with the same payload. Each side runs in its own fresh host, so neither can see the other's
/// ledger. Unbindable payloads are excluded by the spec: the saved route answers them itself (Q-79).
/// </summary>
public sealed class EquivalenceTests
{
    private const string SyncPostRoute = "/api/v1/quickbooks/transactions/validate-and-post";

    /// <summary>Fields only the saved route has (spec 7.2): removed before the bodies are compared.</summary>
    private static readonly string[] Added = ["savedRequestId", "attemptId", "savedRequestStatus"];

    /// <summary>Top-level fields that differ between any two posts or requests: compared as present, not by value.</summary>
    private static readonly string[] PerCall = ["postId", "startedUtc", "finishedUtc", "requestId", "traceId"];

    public static TheoryData<string> Scenarios => new()
    {
        "posted",
        "posted-one-row",
        "held",
        "held-several",
        "held-unknown-payee",
        "dry-run-refused",
        "dry-run-false-refused",
        "model-accounts-refused",
        "control-total-refused",
        "partial",
        "failed",
        "no-response",
        "unavailable",
        "busy-timeout",
    };

    /// <summary>The fixture a scenario sends, and what QuickBooks does with it.</summary>
    private static (string Fixture, Action<FakeQbGateway> Arrange) Scenario(string name) => name switch
    {
        "partial" => (SyncPostFixtures.Partial, g => g.RejectLine = 2),
        "failed" => (SyncPostFixtures.Failed, g => g.RejectLine = 1),
        "no-response" => (
            SyncPostFixtures.NoResponse,
            g => g.Throw = new QuickBooksCallException("ProcessRequest failed (0x80010105)", unchecked((int)0x80010105))),
        "unavailable" => (SyncPostFixtures.PostedOneRow, g => g.Throw = new QuickBooksUnavailableException("simulated")),
        "busy-timeout" => (SyncPostFixtures.PostedOneRow, g => g.Throw = new QuickBooksBusyException("simulated")),
        _ => (name + ".json", _ => { }),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Should_AnswerExactlyAsValidateAndPost_When_TheSamePayloadIsPostedById(string scenario)
    {
        var (fixture, arrange) = Scenario(scenario);

        using var direct = new ApiFactory();
        arrange(direct.Gateway);
        using var directClient = direct.CreateAuthorizedClient();
        using var expected = await directClient.PostAsync(SyncPostRoute, SyncPostFixtures.Content(fixture));
        var expectedBody = await BodyOf(expected);

        using var saved = new SavedRequestTestHost();
        arrange(saved.Factory.Gateway);
        var id = await SavedIdAsync(saved, fixture);
        using var editor = saved.User(DashboardRoles.Editor);
        using var actual = await SavedRequestTestHost.PostActionAsync(editor, id, "post");
        var actualBody = await BodyOf(actual);

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
            $"bodies differ:\nvalidate-and-post: {expectedBody.ToJsonString()}\npost by id:        {actualBody.ToJsonString()}");
        Assert.Equal(direct.Gateway.Requests, saved.Factory.Gateway.Requests);
    }

    private static async Task<long> SavedIdAsync(SavedRequestTestHost host, string fixture)
    {
        var (response, body) = await host.SaveAsync(SyncPostFixtures.Read(fixture));
        using (response)
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        }

        return body.GetProperty("id").GetInt64();
    }

    private static async Task<JsonObject> BodyOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!.AsObject();

    /// <summary>
    /// Each per-call field becomes the marker <c>"*"</c> (a null stays null). Top level only: a posted row's
    /// <c>requestId</c> is its fingerprint, which is deterministic and must match.
    /// </summary>
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
