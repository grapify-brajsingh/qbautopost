using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212: seeds the fake store directly at chosen times, so the summary and the posted filters can be pinned to exact
/// instants (a save through HTTP is stamped with the wall clock).
/// </summary>
internal static class DashboardSeed
{
    public const string Client = "api-client:n8n";
    public const string Admin = "user-7";

    public static async Task<long> SaveAsync(FakeSavedRequestStore store, string reference, DateTime at, DateTime? emailReceivedUtc = null)
    {
        var body = $$"""{"reference":"{{reference}}","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var request = new NewSavedRequest(
            Client, Client, body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, at, 1, null, [], [], null),
            new DuplicateMatch(null, []), at)
        {
            Metadata = SaveMetadata.None with { EmailReceivedUtc = emailReceivedUtc },
        };
        return (await store.CreateAsync(request, default)).Record.Id;
    }

    /// <summary>One attempt finishing with <paramref name="outcome"/> at <paramref name="finished"/>; its result's <c>posted[]</c> holds <paramref name="amounts"/>.</summary>
    public static async Task AttemptAsync(
        FakeSavedRequestStore store, long id, AttemptOutcome outcome, SavedRequestStatus next, DateTime finished,
        params decimal[] amounts)
    {
        var claim = await store.TryClaimAsync(id, null, Client, false, finished.AddMinutes(-1), default);
        var ok = outcome is AttemptOutcome.Posted or AttemptOutcome.Partial;
        var posted = new JsonArray();
        foreach (var amount in amounts)
        {
            posted.Add(new JsonObject { ["txnId"] = $"TX-{posted.Count + 1}", ["amount"] = amount });
        }

        await store.CompleteAttemptAsync(
            claim.AttemptId!.Value,
            new AttemptCompletion(outcome, next, ok ? 200 : 500, ok ? "P-1" : null, new JsonObject { ["posted"] = posted },
                null, ok ? null : "boom", finished),
            default);
    }

    public static async Task<long> PostedAtAsync(FakeSavedRequestStore store, string reference, DateTime finished, params decimal[] amounts)
    {
        var id = await SaveAsync(store, reference, finished.AddDays(-3));
        await AttemptAsync(store, id, AttemptOutcome.Posted, SavedRequestStatus.Posted, finished, amounts);
        return id;
    }

    public static Task DiscardAsync(FakeSavedRequestStore store, long id, DateTime at) =>
        store.ChangeStatusAsync(
            id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, at,
            SavedRequestEvents.Discarded, new JsonObject { ["reason"] = "Sent Twice" }, default);
}
