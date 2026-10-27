using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-909 / FR-A-12: <c>Idempotency-Key</c> on the mutating routes. The failure this prevents is the expensive one —
/// a caller whose connection dropped retries, and the money posts twice.
/// <para>
/// It is the convenience layer, not the safety layer: without a key, G4 and the ledger still catch the repeat (they
/// hold it, which surfaces as a <c>partial</c> batch). With a key, the caller simply gets their first answer back.
/// </para>
/// </summary>
public sealed class IdempotencyApiTests : IDisposable
{
    private const string Route = "/api/v1/quickbooks/transactions";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static object Row(decimal amount = 184.32m, string externalId = "row-1") => new
    {
        externalId,
        kind = "Check",
        date = "2026-08-05",
        amount,
        account = "Chase Checking 4521",
        payee = "HOME DEPOT #6412",
        memo = "HOME DEPOT #6412 PURCHASE",
        last4 = "4521",
    };

    private static object Request(decimal amount = 184.32m, string externalId = "row-1") => new
    {
        reference = "payroll-2026-09",
        dryRun = false,
        controlTotal = amount,
        transactions = new[] { Row(amount, externalId) },
    };

    private HttpClient Client(string? key)
    {
        var client = _factory.CreateAuthorizedClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        }

        return client;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(string? key, object? request = null)
    {
        using var client = Client(key);
        var response = await client.PostAsJsonAsync(Route, request ?? Request(), ApiFactory.Json);
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Should_ReplayTheFirstAnswer_When_TheSameRequestIsSentTwiceWithOneKey()
    {
        var (first, firstBody) = await PostAsync("k1");
        var (second, secondBody) = await PostAsync("k1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // The same batch, not a second one — and the TxnIDs are the first posting's.
        Assert.Equal(firstBody.GetProperty("batchId").GetString(), secondBody.GetProperty("batchId").GetString());
        Assert.Equal(
            firstBody.GetProperty("posted").EnumerateArray().Single().GetProperty("txnId").GetString(),
            secondBody.GetProperty("posted").EnumerateArray().Single().GetProperty("txnId").GetString());
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
        // The point of the whole feature: QuickBooks saw exactly one posting.
        Assert.Single(_factory.Gateway.Writes);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task Should_PostTwice_When_NoKeyIsSent()
    {
        var (first, firstBody) = await PostAsync(null);
        var (second, secondBody) = await PostAsync(null, Request(amount: 99.10m, externalId: "row-2"));

        Assert.NotEqual(firstBody.GetProperty("batchId").GetString(), secondBody.GetProperty("batchId").GetString());
        Assert.Equal(2, _factory.Gateway.Writes.Count);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task Should_Answer409_When_OneKeyIsUsedForADifferentBody()
    {
        var (first, _) = await PostAsync("k1");

        // Posted directly rather than through the helper: ReadProblemAsync needs the body still unread.
        using var client = Client("k1");
        using var second = await client.PostAsJsonAsync(Route, Request(amount: 99.10m, externalId: "row-2"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.ReadProblemAsync();
        Assert.Contains("idempotency-key-reused", problem.GetProperty("detail").GetString());
        // The second request never ran, so only the first posting reached QuickBooks.
        Assert.Single(_factory.Gateway.Writes);
        first.Dispose();
    }

    [Fact]
    public async Task Should_Answer409WithRetryAfter_When_TheSameKeyArrivesWhileTheFirstIsStillRunning()
    {
        // A short synchronous budget so that a middleware which stopped short-circuiting fails this test in seconds
        // with a 202, instead of hanging for the two-minute default behind the wedged first request.
        using var host = _factory.WithSetting("Api:SyncPostTimeoutSeconds", "2");
        _factory.Gateway.Hang = true;
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "k1");
        var first = client.PostAsJsonAsync(Route, Request(), ApiFactory.Json);
        _ = first.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await WaitUntilQuickBooksIsBusyAsync();

        using var secondClient = host.CreateClient();
        secondClient.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        secondClient.DefaultRequestHeaders.Add("Idempotency-Key", "k1");
        var second = await secondClient.PostAsJsonAsync(Route, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("5", second.Headers.GetValues("Retry-After").Single());
        var problem = await second.ReadProblemAsync();
        Assert.Contains("idempotency-in-progress", problem.GetProperty("detail").GetString());
        second.Dispose();
    }

    [Fact]
    public async Task Should_FreeTheKey_When_TheFirstRequestWasRefusedBeforeDoingAnything()
    {
        // A control total that does not add up is a 400: nothing happened, so the caller may fix it and resend with
        // the same key. Holding the key would punish them for a typo.
        var bad = new { reference = "payroll-2026-09", dryRun = false, controlTotal = 1.00m, transactions = new[] { Row() } };
        var (refused, _) = await PostAsync("k1", bad);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var (retried, body) = await PostAsync("k1");

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        refused.Dispose();
        retried.Dispose();
    }

    [Fact]
    public async Task Should_Answer400_When_TheKeyIsLongerThanTheBound()
    {
        // An *empty* header is not tested: HTTP cannot tell one from an absent header, and absent means "no
        // idempotency", which is a legitimate request rather than an error.
        var (response, _) = await PostAsync(new string('k', IdempotencyMiddleware.MaxKeyLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Refused before the request ran, so nothing was sent.
        Assert.Empty(_factory.Gateway.Writes);
        response.Dispose();
    }

    [Fact]
    public async Task Should_ReplayAnUndo_When_TheSameKeyIsSentTwice()
    {
        // FR-A-12 covers undo as well: deleting the same batch twice must not become two trips to QuickBooks.
        var (posted, body) = await PostAsync(null);
        var batchId = Uri.EscapeDataString(body.GetProperty("batchId").GetString()!);
        var deletesBefore = _factory.Gateway.Writes.Count;

        using var client = Client("undo-1");
        using var first = await client.PostAsync($"/api/v1/batches/{batchId}/undo", null);
        using var second = await client.PostAsync($"/api/v1/batches/{batchId}/undo", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
        // One delete message set, not two.
        Assert.Equal(deletesBefore + 1, _factory.Gateway.Writes.Count);
        posted.Dispose();
    }

    [Fact]
    public async Task Should_KeepTheRecordBesideTheBatches_When_AKeyIsUsed()
    {
        var (response, _) = await PostAsync("k1");

        var file = Path.Combine(_factory.Dir.Combine("data", "api-batches"), "idempotency.json");
        Assert.True(File.Exists(file));
        Assert.Contains("k1", File.ReadAllText(file));
        response.Dispose();
    }

    [Fact]
    public async Task Should_NotApplyToAValidate_When_AKeyIsSent()
    {
        // Validation changes nothing, so replaying it would only hide a change in the rules or the ledger.
        using var client = Client("k1");
        using var first = await client.PostAsJsonAsync(Route + "/validate", Request(), ApiFactory.Json);
        using var second = await client.PostAsJsonAsync(Route + "/validate", Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotency-Replayed"));
    }

    private async Task WaitUntilQuickBooksIsBusyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_factory.Gateway.Requests.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
