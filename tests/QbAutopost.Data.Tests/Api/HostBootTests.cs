using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// R14 (T-1010): the real <c>Program</c> wiring, booted against a real PostgreSQL. <c>AddQbAutopostData</c>, the
/// schema check, <c>InFlightRecovery</c> and Identity over the pooled context are otherwise only ever replaced by
/// fakes in the API suite.
/// </summary>
public sealed class HostBootTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task Should_StartReadyAndRecover_When_HostBootsAgainstRealPostgres()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        var inFlight = await SeedPostingRequestAsync(connectionString);
        try
        {
            using var factory = ApiFactory.WithPostgres(connectionString);
            using var client = factory.CreateAuthorizedClient();

            using var response = await client.GetAsync("/api/v1/health/database");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
            Assert.True(factory.Gate.IsReady);
            Assert.IsType<EfSavedRequestStore>(factory.Services.GetRequiredService<ISavedRequestStore>());
            var recovered = await factory.Services.GetRequiredService<ISavedRequestStore>().GetAsync(inFlight, default);
            Assert.Equal(SavedRequestStatus.Unknown, recovered!.Status);
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    /// <summary>A saved request left <c>posting</c>, as a crash mid-post leaves it (spec 10.6).</summary>
    private static async Task<long> SeedPostingRequestAsync(string connectionString)
    {
        await using var services = new ServiceCollection().AddQbAutopostData(connectionString, 30).BuildServiceProvider();
        var store = services.GetRequiredService<ISavedRequestStore>();
        const string body = """
            {"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}
            """;
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        var created = await store.CreateAsync(
            new NewSavedRequest(
                "api-client:n8n", "api-client:n8n", body, payload, read, PayloadCanonicalizer.Sha256(payload),
                read.Request is null ? new HashSet<string>() : DuplicateDetector.RowFingerprints(read.Request, new DirectLimits(), DateOnly.FromDateTime(T0)),
                null, new ValidationSnapshot(ValidationState.Ok, T0, 1, null, [], [], null), new DuplicateMatch(null, []), T0),
            default);
        var claim = await store.TryClaimAsync(created.Record.Id, created.Record.RowVersion, "user:test", false, T0.AddMinutes(1), default);
        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        return created.Record.Id;
    }
}
