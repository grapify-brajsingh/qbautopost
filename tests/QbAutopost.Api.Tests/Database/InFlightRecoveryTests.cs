using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.Database;

/// <summary>Saved-requests spec 10.6: a request left <c>posting</c> by a crash becomes <c>unknown</c>, once.</summary>
public sealed class InFlightRecoveryTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A saved request claimed for posting and never finished, as a crash would leave it.</summary>
    internal static async Task<SavedRequest> SeedPostingAsync(FakeSavedRequestStore store)
    {
        const string body =
            """{"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        var snapshot = new ValidationSnapshot(ValidationState.Ok, Now, 1, null, [], [], null);
        var created = await store.CreateAsync(
            new NewSavedRequest(
                "api-client:n8n", "api-client:n8n", body, payload, read, PayloadCanonicalizer.Sha256(payload),
                new HashSet<string>(), null, snapshot, new DuplicateMatch(null, []), Now),
            default);
        var claim = await store.TryClaimAsync(created.Record.Id, created.Record.RowVersion, "user:1", false, Now, default);
        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        return claim.Record!;
    }

    private static DatabaseGate GateFor(SchemaStatus status) =>
        new(_ => Task.FromResult(status), true, NullLogger<DatabaseGate>.Instance);

    private static InFlightRecovery Recovery(DatabaseGate gate, FakeSavedRequestStore store) =>
        new(gate, store, new FixedClock { UtcNow = Now }, NullLogger<InFlightRecovery>.Instance);

    [Fact]
    public async Task Should_MarkPostingRequestsUnknown_When_GateIsReadyAtStart()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var gate = GateFor(new SchemaStatus(true, true, [], null));

        await Recovery(gate, store).StartAsync(default);

        Assert.Equal(SavedRequestStatus.Unknown, (await store.GetAsync(seeded.Id, default))!.Status);
    }

    [Fact]
    public async Task Should_WaitForTheGate_When_DatabaseIsDownAtStart()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var up = false;
        var gate = new DatabaseGate(
            _ => Task.FromResult(up ? new SchemaStatus(true, true, [], null) : new SchemaStatus(false, false, [], "unreachable")),
            true,
            NullLogger<DatabaseGate>.Instance);

        await Recovery(gate, store).StartAsync(default);
        Assert.Equal(SavedRequestStatus.Posting, (await store.GetAsync(seeded.Id, default))!.Status);

        up = true;
        await gate.RefreshAsync(default);

        await Eventually(async () => (await store.GetAsync(seeded.Id, default))!.Status == SavedRequestStatus.Unknown);
    }

    [Fact]
    public async Task Should_DoNothing_When_DatabaseIsNotConfigured()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var gate = new DatabaseGate(null, true, NullLogger<DatabaseGate>.Instance);

        await Recovery(gate, store).StartAsync(default);

        Assert.Equal(SavedRequestStatus.Posting, (await store.GetAsync(seeded.Id, default))!.Status);
    }

    [Fact]
    public async Task Should_RunOnlyOnce_When_GateBecomesReadyAgainLater()
    {
        var store = new FakeSavedRequestStore();
        var gate = GateFor(new SchemaStatus(true, true, [], null));
        await Recovery(gate, store).StartAsync(default);
        var seeded = await SeedPostingAsync(store);

        gate.Set(DatabaseState.Unreachable);
        await gate.RefreshAsync(default);

        Assert.Equal(SavedRequestStatus.Posting, (await store.GetAsync(seeded.Id, default))!.Status);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met within 5 seconds");
            await Task.Delay(10);
        }
    }
}
