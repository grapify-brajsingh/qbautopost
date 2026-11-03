using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Api.Database;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1012 (saved-requests spec 6.1, 6.4, 9, 10.1, 10.7-10.9, 5.3): <c>POST /api/v1/saved-requests</c>. Anything that
/// is a JSON object is stored; what is wrong with it becomes a flag for a person, never a refused save.
/// </summary>
public sealed class SaveApiTests : IDisposable
{
    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    [Fact]
    public async Task Should_Return201WithLocation_When_N8nKeySavesGoodPayload()
    {
        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetInt64();
        Assert.Equal($"/api/v1/saved-requests/{id}", response.Headers.Location?.ToString());
        Assert.Equal($"SR-{id:000000}", body.GetProperty("publicRef").GetString());
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("ok", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.Equal(2, body.GetProperty("validation").GetProperty("counts").GetProperty("postable").GetInt32());
        Assert.False(body.GetProperty("duplicate").GetProperty("flagged").GetBoolean());
    }

    [Fact]
    public async Task Should_SaveWithNeedsAttention_When_KindUnknown()
    {
        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-unknown-kind.json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("unreadable", body.GetProperty("validation").GetProperty("state").GetString());
        var error = Assert.Single(body.GetProperty("validation").GetProperty("errors").EnumerateArray()).GetString();
        Assert.StartsWith("$.transactions[3].kind", error);
    }

    [Fact]
    public async Task Should_SaveWithNeedsAttention_When_ControlTotalWrong()
    {
        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-control-total-wrong.json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("refused", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.Contains(
            body.GetProperty("validation").GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.StartsWith("control-total-mismatch", StringComparison.Ordinal));
        // Rule 3: the stored control total is the caller's number, never one the code worked out.
        Assert.Equal(999.99m, (await StoredAsync(body.GetProperty("id").GetInt64())).ControlTotal);
    }

    /// <summary>R33: jsonb cannot hold U+0000, so the save is refused up front, naming why, and nothing is stored.</summary>
    [Theory]
    [InlineData("n8n-nul-in-memo.json")]
    [InlineData("n8n-nul-in-key.json")]
    public async Task Should_Return400PayloadNotStorable_When_ThePayloadContainsUPlus0000(string fixture)
    {
        var (response, problem) = await _host.SaveAsync(SavedRequestTestHost.Fixture(fixture));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("payload-not-storable", problem.GetProperty("type").GetString());
        Assert.Contains("contains U+0000, which the database cannot store", problem.GetProperty("detail").GetString());
        Assert.Equal(0, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    /// <summary>R33: a control total numeric(18,2) cannot hold is saved, kept in the payload, NULL in the column, and flagged.</summary>
    [Fact]
    public async Task Should_SaveWithNullControlTotalColumn_When_ControlTotalOverflowsTheColumn()
    {
        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-control-total-overflow.json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Contains(
            body.GetProperty("validation").GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.StartsWith("control-total-mismatch", StringComparison.Ordinal));
        var stored = await StoredAsync(body.GetProperty("id").GetInt64());
        Assert.Null(stored.ControlTotal);
        Assert.Equal(10000000000000000m, stored.CurrentPayload.GetProperty("controlTotal").GetDecimal());
    }

    [Fact]
    public async Task Should_SaveWithNeedsAttention_When_DryRunPresent()
    {
        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-dry-run.json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("refused", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.Contains(
            body.GetProperty("validation").GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.StartsWith("dry-run-not-supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SaveWithUnavailableSnapshot_When_RulesFileMissing()
    {
        using var host = _host.Factory.WithSetting("Company:RulesFile", _host.Factory.Dir.Combine("no-such-rules.json"));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _host.N8nKey);

        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"), client: client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("unavailable", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.False(body.GetProperty("needsAttention").GetBoolean());
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("just some text")]
    [InlineData("\"a string\"")]
    [InlineData("")]
    public async Task Should_Return400NotAJsonObject_When_BodyIsArrayOrText(string body)
    {
        var (response, problem) = await _host.SaveAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("not-a-json-object", problem.GetProperty("type").GetString());
        Assert.Equal(0, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    [Fact]
    public async Task Should_Return413_When_BodyOverLimit()
    {
        using var host = _host.Factory.WithSetting("Api:MaxRequestBodyBytes", "256");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _host.N8nKey);

        var (response, _) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"), client: client);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    [Fact]
    public async Task Should_Return200Existing_When_SameIdempotencyKeyAndBody()
    {
        var body = SavedRequestTestHost.Fixture("n8n-good.json");
        var (first, created) = await _host.SaveAsync(body, "message-id-1");

        var (second, replayed) = await _host.SaveAsync(body, "message-id-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(created.GetProperty("id").GetInt64(), replayed.GetProperty("id").GetInt64());
        Assert.Equal(1, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    [Fact]
    public async Task Should_Return409_When_SameKeyDifferentBody()
    {
        await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"), "message-id-1");

        var (response, problem) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-dry-run.json"), "message-id-1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency-key-reused", problem.GetProperty("type").GetString());
        Assert.Equal(1, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    [Fact]
    public async Task Should_FlagSamePayload_When_SavedTwiceWithoutKey()
    {
        var body = SavedRequestTestHost.Fixture("n8n-good.json");
        var (_, first) = await _host.SaveAsync(body);

        var (response, second) = await _host.SaveAsync(body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var duplicate = second.GetProperty("duplicate");
        Assert.True(duplicate.GetProperty("flagged").GetBoolean());
        Assert.Equal(first.GetProperty("id").GetInt64(), duplicate.GetProperty("of").GetInt64());
        Assert.Contains(duplicate.GetProperty("reasons").EnumerateArray(), r => r.GetString() == DuplicateReasons.SamePayload);
    }

    [Fact]
    public async Task Should_SetCreatedByFromKey_When_BodyCarriesCreatedBy()
    {
        var body = SavedRequestTestHost.Fixture("n8n-good.json")
            .Replace("\"reference\":", "\"createdBy\": \"mallory\", \"updatedBy\": \"mallory\", \"reference\":", StringComparison.Ordinal);

        var (_, saved) = await _host.SaveAsync(body);

        var stored = await StoredAsync(saved.GetProperty("id").GetInt64());
        Assert.Equal("api-client:" + SavedRequestTestHost.N8nClientId, stored.CreatedBy);
        Assert.Equal("api-client:" + SavedRequestTestHost.N8nClientId, stored.UpdatedBy);
        Assert.Equal("api-client:" + SavedRequestTestHost.N8nClientId, stored.Source);
    }

    /// <summary>Spec 10.1: the store honours the key per source; the file-backed middleware must not answer first.</summary>
    [Fact]
    public void Should_LeaveIdempotencyToTheStore_When_Saving() =>
        Assert.False(ApiRoutes.IsIdempotent("POST", "/api/v1/saved-requests"));

    [Fact]
    public async Task Should_Return503_When_DatabaseGateNotReady()
    {
        using var client = _host.N8n();
        await client.GetAsync("/health"); // starts the host; its start-up check makes the gate Ready
        _host.Factory.Gate.Set(DatabaseState.Unreachable);

        var (response, problem) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"), client: client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database-unavailable", problem.GetProperty("type").GetString());
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(0, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    /// <summary>R24: whatever stops the plan, the save still succeeds and the body names no path.</summary>
    [Fact]
    public async Task Should_SaveWithUnavailableSnapshot_When_PlannerThrowsAnything()
    {
        const string secretPath = @"C:\secret\serverules.json";
        using var host = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<DirectPostRunner>();
            services.AddSingleton<DirectPostRunner>(sp =>
                ActivatorUtilities.CreateInstance<ThrowingRunner>(sp, new InvalidOperationException("bad state at " + secretPath)));
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _host.N8nKey);

        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"), client: client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var validation = body.GetProperty("validation");
        Assert.Equal("unavailable", validation.GetProperty("state").GetString());
        Assert.Equal("validation could not run", validation.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("needsAttention").GetBoolean());
        Assert.DoesNotContain("secret", body.GetRawText());
        Assert.Equal(1, (await Store.StatsAsync(CancellationToken.None)).Total);
    }

    private sealed class ThrowingRunner(
        Exception failure, PipelineOptions options, DirectPlanner planner, ApiBatchStore batches, IQbGateway gateway,
        IClock clock, ILogger<DirectPostRunner> log, QbAutopost.Core.Abstractions.IQbListsSource lists)
        : DirectPostRunner(options, planner, batches, gateway, clock, log, lists)
    {
        public override DirectPlan Plan(DirectRequest request) => throw failure;
    }
}
