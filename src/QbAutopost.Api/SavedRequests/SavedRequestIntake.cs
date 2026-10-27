using System.Text.Json;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// Saved-requests spec 6.1, 6.3, 6.4, 9: what happens to a payload on its way into the store. It is read
/// (<see cref="SavedPayloadReader"/>), validated (<see cref="SavedRequestValidator"/>), canonicalised and compared with
/// the other recent requests (<see cref="DuplicateDetector"/>), and stored with the caller as its author. Nothing here
/// refuses a payload for what it says: an unreadable or refused payload is stored and flagged for a person.
/// <para>
/// Rule 3: nothing here computes money. The control total and the amounts are the caller's, stored as sent.
/// </para>
/// </summary>
public sealed class SavedRequestIntake(
    ISavedRequestStore store,
    SavedRequestValidator validator,
    DirectLimits limits,
    IOptions<AppSettings> settings,
    IClock clock)
{
    /// <summary>The nesting limit of a saved body (spec 10.9: a hostile payload fails to parse, it does not recurse).</summary>
    public const int MaxDepth = 64;

    /// <summary>Spec 5.2: the source of a request a person created in the dashboard.</summary>
    public const string SourceUserPrefix = "user:";

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxDepth };

    private static readonly IReadOnlySet<string> NoFingerprints = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Spec 6.1 step 2: true only for text that parses as one JSON object.</summary>
    public static bool TryParseObject(string text, out JsonElement payload)
    {
        payload = default;
        try
        {
            using var document = JsonDocument.Parse(text, ParseOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Spec 6.1 step 3 onwards, for a body <see cref="TryParseObject"/> accepted. The source and the author are the
    /// caller (spec 5.3); a <c>createdBy</c> in the body is just another unknown field, kept and ignored.
    /// </summary>
    public async Task<CreateResult> SaveAsync(string rawBody, string? idempotencyKey, Caller caller, CancellationToken ct)
    {
        if (!TryParseObject(rawBody, out var payload))
        {
            throw new ArgumentException("The body is not a JSON object; the route answers 400 before this.", nameof(rawBody));
        }

        var now = clock.UtcNow;
        var read = SavedPayloadReader.Read(payload);
        var snapshot = validator.Validate(read, 1, now);
        var canonical = PayloadCanonicalizer.Sha256(payload);
        var fingerprints = Fingerprints(read, now);
        var duplicate = await DetectAsync(canonical, read, fingerprints, null, now, ct);

        return await store.CreateAsync(
            new NewSavedRequest(
                SourceOf(caller), caller.Actor, rawBody, payload, read, canonical, fingerprints, idempotencyKey,
                snapshot, duplicate, now),
            ct);
    }

    /// <summary>
    /// Spec 6.3: a full replacement payload becomes version n+1 with its change list. The store decides not found,
    /// stale, wrong status and unchanged, in that order, atomically.
    /// </summary>
    public async Task<MutationResult> EditAsync(
        long id, int expectedRowVersion, JsonElement payload, string? note, Caller caller, CancellationToken ct)
    {
        var current = await store.GetAsync(id, ct);
        if (current is null)
        {
            return new MutationResult(MutationKind.NotFound, null);
        }

        var now = clock.UtcNow;
        var read = SavedPayloadReader.Read(payload);
        var snapshot = validator.Validate(read, current.CurrentVersion + 1, now);
        var canonical = PayloadCanonicalizer.Sha256(payload);
        var fingerprints = Fingerprints(read, now);
        var duplicate = await DetectAsync(canonical, read, fingerprints, id, now, ct);
        var changes = PayloadDiff.Compute(current.CurrentPayload, payload);

        return await store.AppendVersionAsync(
            id,
            expectedRowVersion,
            new NewVersion(payload, read, canonical, fingerprints, changes, note, snapshot, duplicate, caller.Actor, now),
            ct);
    }

    /// <summary>Spec 6.4: re-runs validation and the duplicate rules for the current version and stores the snapshot.</summary>
    public async Task<MutationResult> RevalidateAsync(long id, Caller caller, CancellationToken ct)
    {
        var current = await store.GetAsync(id, ct);
        if (current is null)
        {
            return new MutationResult(MutationKind.NotFound, null);
        }

        var now = clock.UtcNow;
        var read = SavedPayloadReader.Read(current.CurrentPayload);
        var snapshot = validator.Validate(read, current.CurrentVersion, now);
        var fingerprints = Fingerprints(read, now);
        var duplicate = await DetectAsync(current.CanonicalSha256, read, fingerprints, id, now, ct);

        return await store.SetValidationAsync(id, snapshot, duplicate, caller.Actor, now, ct);
    }

    /// <summary>Spec 5.2: <c>api-client:&lt;clientId&gt;</c> for a key, <c>user:&lt;userId&gt;</c> for a person.</summary>
    public static string SourceOf(Caller caller) =>
        caller.Kind == CallerKind.User ? SourceUserPrefix + caller.Id : ActorNames.ClientPrefix + caller.Id;

    /// <summary>R6: the CLAUDE.md row fingerprints, only for a readable payload (spec 9, overlapping-rows).</summary>
    private IReadOnlySet<string> Fingerprints(PayloadRead read, DateTime nowUtc) =>
        read.Request is { } request
            ? DuplicateDetector.RowFingerprints(request, limits, DateOnly.FromDateTime(nowUtc))
            : NoFingerprints;

    private async Task<DuplicateMatch> DetectAsync(
        string canonical, PayloadRead read, IReadOnlySet<string> fingerprints, long? excludeId, DateTime nowUtc,
        CancellationToken ct)
    {
        var since = nowUtc.AddDays(-Math.Max(0, settings.Value.SavedRequests.DuplicateLookbackDays));
        var candidates = await store.DuplicateCandidatesAsync(since, excludeId, ct);
        return DuplicateDetector.Detect(canonical, read.Request?.Reference, fingerprints, candidates);
    }
}
