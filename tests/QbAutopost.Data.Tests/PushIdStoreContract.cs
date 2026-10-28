using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1108: what every <see cref="ISavedRequestStore"/> must do with a repeated <c>pushId</c> (spec
/// saved-requests-dashboard-v2 5.2): the same body replays the existing record, a different body is push-id-reused,
/// and a matching Idempotency-Key decides alone.
/// </summary>
public abstract class PushIdStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    protected static readonly Guid PushA = Guid.Parse("6f1c2a4e-9b7d-4c3e-8a21-0d5e6f7a8b9c");
    protected static readonly Guid PushB = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    protected abstract ISavedRequestStore CreateStore();

    protected static string Body(string reference = "R1") =>
        $$"""{"reference":"{{reference}}","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";

    protected static NewSavedRequest NewRequest(string body, Guid? pushId, string? key = null)
    {
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        return new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", body, payload, read, PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), key, new ValidationSnapshot(ValidationState.Ok, T0, 1, null, [], [], null),
            new DuplicateMatch(null, []), T0)
        {
            Metadata = new SaveMetadata(null, null, null, null, pushId, []),
        };
    }

    [ContractFact]
    public async Task Should_ReplayExisting_When_SamePushIdSameBody()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(NewRequest(Body(), PushA), default);

        var second = await store.CreateAsync(NewRequest(Body(), PushA), default);

        Assert.Equal(CreateKind.Created, first.Kind);
        Assert.Equal(CreateKind.Replayed, second.Kind);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(1, (await store.StatsAsync(default)).Total);
    }

    [ContractFact]
    public async Task Should_ReportPushIdReused_When_SamePushIdDifferentBody()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(NewRequest(Body(), PushA), default);

        var second = await store.CreateAsync(NewRequest(Body("OTHER"), PushA), default);

        Assert.Equal(CreateKind.PushIdReused, second.Kind);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal("R1", second.Record.Reference);
        Assert.Equal(1, (await store.StatsAsync(default)).Total);
    }

    [ContractFact]
    public async Task Should_SaveBoth_When_PushIdsDiffer()
    {
        var store = CreateStore();
        await store.CreateAsync(NewRequest(Body(), PushA), default);

        var second = await store.CreateAsync(NewRequest(Body(), PushB), default);

        Assert.Equal(CreateKind.Created, second.Kind);
        Assert.Equal(2, (await store.StatsAsync(default)).Total);
    }

    [ContractFact]
    public async Task Should_ReplayByKey_When_KeyAndBodyMatchAndPushIdIsNew()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(NewRequest(Body(), PushA, "k1"), default);

        var second = await store.CreateAsync(NewRequest(Body(), PushB, "k1"), default);

        Assert.Equal(CreateKind.Replayed, second.Kind);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(1, (await store.StatsAsync(default)).Total);
    }

    [ContractFact]
    public async Task Should_LetKeyDecideAlone_When_IdempotencyKeyMatches()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(NewRequest(Body(), PushA, "k1"), default);
        var other = await store.CreateAsync(NewRequest(Body("OTHER"), PushB, "k2"), default);

        // Same key, different body: reused, whether the pushId is new or held by another record (which, with a
        // different body, would otherwise be push-id-reused naming that other record).
        var freshPush = await store.CreateAsync(NewRequest(Body("THIRD"), Guid.NewGuid(), "k1"), default);
        var heldPush = await store.CreateAsync(NewRequest(Body("FOURTH"), PushB, "k1"), default);

        Assert.Equal(CreateKind.KeyReused, freshPush.Kind);
        Assert.Equal(first.Record.Id, freshPush.Record.Id);
        Assert.Equal(CreateKind.KeyReused, heldPush.Kind);
        Assert.Equal(first.Record.Id, heldPush.Record.Id);
        Assert.NotEqual(first.Record.Id, other.Record.Id);
    }

    [ContractFact]
    public async Task Should_CheckPushId_When_KeyMatchesNothing()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(NewRequest(Body(), PushA, "k1"), default);

        var second = await store.CreateAsync(NewRequest(Body("OTHER"), PushA, "k2"), default);

        Assert.Equal(CreateKind.PushIdReused, second.Kind);
        Assert.Equal(first.Record.Id, second.Record.Id);
    }
}
