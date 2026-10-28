using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.SavedRequests;
using Xunit.Abstractions;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1107 step 5 (spec saved-requests-dashboard-v2 8.4, builder's call): a v2 list page, with its history counts and
/// post summaries computed in the query, stays under 200 ms at the default page size over 1000 requests. If it did
/// not, <c>historyCount</c> would become a stored count.
/// </summary>
[Trait("Category", "Perf")]
public sealed class ListV2PerformanceTests(PostgresFixture postgres, ITestOutputHelper output)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int Requests = 1000;
    private const int DefaultPageSize = 25;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    private ServiceProvider? _services;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        var connectionString = _connectionString = await postgres.CreateDatabaseAsync();
        _services = new ServiceCollection().AddQbAutopostData(connectionString, commandTimeoutSeconds: 60).BuildServiceProvider();
        await PostgresFixture.MigrateAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_connectionString is not null)
        {
            Api.TestDatabase.Release(_connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_ListPageUnder200ms_When_1000Requests()
    {
        var store = _services!.GetRequiredService<ISavedRequestStore>();
        await SeedAsync(store);
        var v2 = Assert.IsAssignableFrom<ISavedRequestV2Store>(store);
        var query = new SavedRequestQueryV2(
            new SavedRequestQuery([], null, null, null, null, null, null, "-received", 1, DefaultPageSize),
            null, null, null, null, null, null, null);

        await v2.ListV2Async(query, default); // warm-up: connection, plans, JIT
        var timings = new List<TimeSpan>();
        PagedResult<SavedRequestSummary>? page = null;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            page = await v2.ListV2Async(query, default);
            timings.Add(watch.Elapsed);
        }

        var median = timings.Order().ElementAt(timings.Count / 2);
        output.WriteLine($"v2 list page of {DefaultPageSize} over {Requests} requests: median {median.TotalMilliseconds:0.0} ms " +
                         $"(runs: {string.Join(", ", timings.Select(t => t.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))})");
        Assert.Equal(Requests, page!.Total);
        Assert.Equal(DefaultPageSize, page.Items.Count);
        Assert.All(page.Items, r => Assert.True(r.HistoryCount >= 2));
        Assert.True(median < Budget, $"median {median.TotalMilliseconds:0.0} ms is over the {Budget.TotalMilliseconds} ms budget");
    }

    /// <summary>1000 requests of 5 rows; every one edited once (two changes) and every third one posted.</summary>
    private static async Task SeedAsync(ISavedRequestStore store)
    {
        for (var i = 0; i < Requests; i++)
        {
            var at = T0.AddMinutes(i);
            var body1 = Body(i, 5);
            var payload1 = JsonDocument.Parse(body1).RootElement.Clone();
            var created = (await store.CreateAsync(
                new NewSavedRequest(
                    "api-client:n8n", "api-client:n8n", body1, payload1, SavedPayloadReader.Read(payload1),
                    PayloadCanonicalizer.Sha256(payload1), new HashSet<string>(), null,
                    new ValidationSnapshot(ValidationState.Ok, at, 1, null, [], [], null), new DuplicateMatch(null, []), at)
                {
                    Metadata = SaveMetadata.None with
                    {
                        EmailSubject = $"Statement {i}", EmailReceivedUtc = at.AddSeconds(-30), PushId = Guid.NewGuid(),
                    },
                },
                default)).Record;

            var body2 = Body(i, 6);
            var payload2 = JsonDocument.Parse(body2).RootElement.Clone();
            var edited = (await store.AppendVersionAsync(
                created.Id, created.RowVersion,
                new NewVersion(
                    payload2, SavedPayloadReader.Read(payload2), PayloadCanonicalizer.Sha256(payload2, PayloadRules.V2),
                    new HashSet<string>(), PayloadDiff.Compute(payload1, payload2, PayloadRules.V2), null,
                    new ValidationSnapshot(ValidationState.Ok, at, 2, null, [], [], null), new DuplicateMatch(null, []),
                    "user-7", at.AddSeconds(10)),
                default)).Record!;

            if (i % 3 == 0)
            {
                var claim = await store.TryClaimAsync(edited.Id, null, "user-7", false, at.AddSeconds(20), default);
                await store.CompleteAttemptAsync(
                    claim.AttemptId!.Value,
                    new AttemptCompletion(
                        AttemptOutcome.Posted, SavedRequestStatus.Posted, 200, $"P-{i}", null, new JsonObject { ["posted"] = 6 },
                        null, at.AddSeconds(25)),
                    default);
            }
        }
    }

    private static string Body(int i, int rows)
    {
        var list = string.Join(',', Enumerable.Range(1, rows).Select(r =>
            $$"""{"externalId":"e{{r}}","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Payee {{i}}","memo":"rent"}"""));
        return $$"""{"reference":"REF-{{i}}","controlTotal":{{rows * 10}}.00,"transactions":[{{list}}]}""";
    }
}
