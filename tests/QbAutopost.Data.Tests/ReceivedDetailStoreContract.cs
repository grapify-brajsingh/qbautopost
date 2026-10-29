using System.Text.Json;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1213 (dashboard-api-m12 7.5, R-14.5): the <c>created</c> event of an ordinary save carries
/// <c>{ "source": "email" }</c> when the save has email metadata and null otherwise, and the audit feed's
/// <c>received</c> passes it through, on every store.
/// </summary>
public abstract class ReceivedDetailStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    protected abstract ISavedRequestStore CreateStore();

    private static NewSavedRequest NewRequest(SaveMetadata metadata, string reference)
    {
        var body = $$"""{"reference":"{{reference}}","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        return new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, T0, 1, null, [], [], null),
            new DuplicateMatch(null, []), T0)
        {
            Metadata = metadata,
        };
    }

    private static async Task<SavedRequestEvent> CreatedEventAsync(ISavedRequestStore store, long id) =>
        Assert.Single(await store.EventsAsync(id, default), e => e.Kind == SavedRequestEvents.Created);

    private static async Task<AuditEvent> ReceivedAsync(ISavedRequestStore store, long id) =>
        Assert.Single(
            (await ((ISavedRequestAuditStore)store).AuditEventsAsync(
                new AuditQuery(null, null, null, id, null, null, [AuditActions.Received], null, false, 1, 50), default)).Items);

    [ContractFact]
    public async Task Should_WriteEmailSource_When_SaveHasEmailMetadata()
    {
        var store = CreateStore();
        var metadata = new SaveMetadata("August statement", null, null, null, null, []);

        var id = (await store.CreateAsync(NewRequest(metadata, "EMAIL"), default)).Record.Id;

        Assert.Equal("""{"source":"email"}""", (await CreatedEventAsync(store, id)).Detail?.ToJsonString());
        Assert.Equal("""{"source":"email"}""", (await ReceivedAsync(store, id)).Detail?.ToJsonString());
    }

    [ContractFact]
    public async Task Should_WriteNullDetail_When_SaveHasNoEmailMetadata()
    {
        var store = CreateStore();

        var id = (await store.CreateAsync(NewRequest(SaveMetadata.None, "PLAIN"), default)).Record.Id;

        Assert.Null((await CreatedEventAsync(store, id)).Detail);
        Assert.Null((await ReceivedAsync(store, id)).Detail);
    }
}
