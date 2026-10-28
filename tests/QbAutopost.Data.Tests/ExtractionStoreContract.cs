using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>T-1209 (dashboard-api-m12 6.6): the extraction records, pinned on every store.</summary>
public abstract class ExtractionStoreContract
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>One object that is both stores, as in the application.</summary>
    protected abstract (ISavedRequestStore Requests, IUploadStore Uploads) CreateStores();

    private static StatementExtraction Extraction(Guid id, DateTime expires, long? savedRequestId = null) =>
        new(id, "user-7", "march.pdf", 12345, new string('a', 64), 3, "vendor/model-x", "extract-v1",
            JsonNode.Parse("""{"isBankStatement":true,"reference":"MAR","controlTotal":null,"transactions":[{"kind":"deposit","date":"2026-03-01","amount":12.50,"memo":"x"}]}""")!.AsObject(),
            Now.AddHours(-1), expires, savedRequestId);

    private static async Task<long> SaveAsync(ISavedRequestStore store)
    {
        var body = """{"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var request = new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, Now, 1, null, [], [], null),
            new DuplicateMatch(null, []), Now);
        return (await store.CreateAsync(request, default)).Record.Id;
    }

    [ContractFact]
    public async Task Should_RoundTripExtraction_When_Created()
    {
        var (_, uploads) = CreateStores();
        var created = Extraction(Guid.NewGuid(), Now.AddHours(23));

        await uploads.CreateExtractionAsync(created, default);
        var read = await uploads.GetExtractionAsync(created.Id, default);

        Assert.NotNull(read);
        Assert.Equal(created with { Payload = read!.Payload }, read);
        Assert.True(JsonNode.DeepEquals(created.Payload, read.Payload));
        Assert.Equal(DateTimeKind.Utc, read.ExtractedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, read.ExpiresUtc.Kind);
    }

    [ContractFact]
    public async Task Should_DeleteOnlyUnsavedExpired_When_DeletingExpired()
    {
        var (requests, uploads) = CreateStores();
        var savedRequest = await SaveAsync(requests);
        var expiredUnsaved = Extraction(Guid.NewGuid(), Now.AddMinutes(-1));
        var expiredSaved = Extraction(Guid.NewGuid(), Now.AddMinutes(-1), savedRequest);
        var live = Extraction(Guid.NewGuid(), Now.AddHours(1));
        foreach (var e in new[] { expiredUnsaved, expiredSaved, live })
        {
            await uploads.CreateExtractionAsync(e, default);
        }

        var deleted = await uploads.DeleteExpiredExtractionsAsync(Now, default);

        Assert.Equal(1, deleted);
        Assert.Null(await uploads.GetExtractionAsync(expiredUnsaved.Id, default));
        Assert.NotNull(await uploads.GetExtractionAsync(expiredSaved.Id, default));
        Assert.NotNull(await uploads.GetExtractionAsync(live.Id, default));
    }

    [ContractFact]
    public async Task Should_ReturnNull_When_ExtractionUnknown()
    {
        var (_, uploads) = CreateStores();

        Assert.Null(await uploads.GetExtractionAsync(Guid.NewGuid(), default));
    }
}
