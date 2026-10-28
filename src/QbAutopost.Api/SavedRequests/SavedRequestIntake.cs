using System.Text.Json;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
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
    /// R33: why PostgreSQL could not store <paramref name="payload"/> as <c>jsonb</c>, or null when it can. jsonb refuses
    /// U+0000 in any string, a property name included, and the refusal used to look like an outage (503 for ever, the
    /// payload lost). It is found here, before any store call, so the fake and the EF store refuse the same payloads.
    /// The answer names the JSON path, never the text around it.
    /// </summary>
    public static string? NotStorableReason(JsonElement payload) => FindNul(payload, "$");

    private static string? FindNul(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString()!.Contains('\0', StringComparison.Ordinal):
                return $"{path} contains U+0000, which the database cannot store";
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains('\0', StringComparison.Ordinal))
                    {
                        return $"a property name in {path} contains U+0000, which the database cannot store";
                    }

                    if (FindNul(property.Value, $"{path}.{property.Name}") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FindNul(item, $"{path}[{index++}]") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Spec 6.1 step 3 onwards, for a body <see cref="TryParseObject"/> accepted. The source and the author are the
    /// caller (spec 5.3); a <c>createdBy</c> in the body is just another unknown field, kept and ignored.
    /// <para>
    /// Dashboard v2 (spec 5, 7, 7.1): a v2 save gives rows without an <c>externalId</c> a <c>row-&lt;n&gt;</c> id
    /// before version 1 is written, reads <c>email</c> and <c>pushId</c> from the body as received, and hashes the
    /// assigned payload by the v2 rules. The original body is kept byte for byte either way. A v1 save does none of
    /// that. Both store the v2 snapshot beside the v1 one (spec 6.4); v1 answers never show it.
    /// </para>
    /// </summary>
    public async Task<CreateResult> SaveAsync(
        string rawBody, string? idempotencyKey, Caller caller, CancellationToken ct, ApiFlavor flavor = ApiFlavor.V1)
    {
        if (!TryParseObject(rawBody, out var payload))
        {
            throw new ArgumentException("The body is not a JSON object; the route answers 400 before this.", nameof(rawBody));
        }

        GuardStorable(payload);

        var v2 = flavor == ApiFlavor.V2;
        var version1 = v2 ? ExternalIdAssigner.AssignOnSave(payload) : payload;
        var metadata = v2 ? SaveMetadataReader.Read(payload) : SaveMetadata.None;

        var now = clock.UtcNow;
        var read = SavedPayloadReader.Read(version1);
        var (snapshot, snapshotV2) = validator.ValidateBoth(read, NotPostedRows.Read(version1), 1, now);
        var canonical = PayloadCanonicalizer.Sha256(version1, v2 ? PayloadRules.V2 : PayloadRules.V1);
        var fingerprints = Fingerprints(read, now);
        var duplicate = await DetectAsync(canonical, read, fingerprints, null, now, ct);

        return await store.CreateAsync(
            new NewSavedRequest(
                SourceOf(caller), caller.Actor, rawBody, version1, read, canonical, fingerprints, idempotencyKey,
                snapshot, duplicate, now)
            {
                Metadata = metadata,
                SnapshotV2 = snapshotV2,
            },
            ct);
    }

    /// <summary>
    /// Spec 6.3: a full replacement payload becomes version n+1 with its change list. The store decides not found,
    /// stale, wrong status and unchanged, in that order, atomically.
    /// <para>
    /// Dashboard v2 (spec 6.4, 7, 7.1): a v2 edit gives rows without an <c>externalId</c> an <c>edit&lt;v&gt;-&lt;n&gt;</c>
    /// id, and hashes and diffs the assigned payload by the v2 rules. A v1 edit does none of that. Both store the v2
    /// snapshot beside the v1 one, which is computed exactly as before.
    /// </para>
    /// </summary>
    public async Task<MutationResult> EditAsync(
        long id, int expectedRowVersion, JsonElement payload, string? note, Caller caller, CancellationToken ct,
        ApiFlavor flavor = ApiFlavor.V1)
    {
        var current = await store.GetAsync(id, ct);
        if (current is null)
        {
            return new MutationResult(MutationKind.NotFound, null);
        }

        var v2 = flavor == ApiFlavor.V2;
        var number = current.CurrentVersion + 1;
        var next = v2 ? ExternalIdAssigner.AssignOnEdit(payload, number) : payload;
        var rules = v2 ? PayloadRules.V2 : PayloadRules.V1;

        var now = clock.UtcNow;
        var read = SavedPayloadReader.Read(next);
        var (snapshot, snapshotV2) = validator.ValidateBoth(read, NotPostedRows.Read(next), number, now, id);
        var canonical = PayloadCanonicalizer.Sha256(next, rules);
        var fingerprints = Fingerprints(read, now);
        var duplicate = await DetectAsync(canonical, read, fingerprints, id, now, ct);
        var changes = PayloadDiff.Compute(current.CurrentPayload, next, rules);

        return await store.AppendVersionAsync(
            id,
            expectedRowVersion,
            new NewVersion(next, read, canonical, fingerprints, changes, note, snapshot, duplicate, caller.Actor, now)
            {
                SnapshotV2 = snapshotV2,
            },
            ct);
    }

    /// <summary>
    /// Spec 6.4: re-runs validation and the duplicate rules for the current version and stores both snapshots. The
    /// flavor of the route does not change what is stored; v1 and v2 differ only in which snapshot they show.
    /// </summary>
    public async Task<MutationResult> RevalidateAsync(long id, Caller caller, CancellationToken ct)
    {
        var current = await store.GetAsync(id, ct);
        if (current is null)
        {
            return new MutationResult(MutationKind.NotFound, null);
        }

        var now = clock.UtcNow;
        var read = SavedPayloadReader.Read(current.CurrentPayload);
        var (snapshot, snapshotV2) = validator.ValidateBoth(
            read, NotPostedRows.Read(current.CurrentPayload), current.CurrentVersion, now, id);
        var fingerprints = Fingerprints(read, now);
        var duplicate = await DetectAsync(current.CanonicalSha256, read, fingerprints, id, now, ct);

        return await store.SetValidationAsync(id, snapshot, snapshotV2, duplicate, caller.Actor, now, ct);
    }

    /// <summary>The routes answer <c>400 payload-not-storable</c> before this; reaching the store with such a payload is a bug.</summary>
    private static void GuardStorable(JsonElement payload)
    {
        if (NotStorableReason(payload) is { } reason)
        {
            throw new ArgumentException($"The payload is not storable ({reason}); the route answers 400 before this.", nameof(payload));
        }
    }

    /// <summary>Spec 5.2: <c>api-client:&lt;clientId&gt;</c> for a key, <c>user:&lt;userId&gt;</c> for a person.</summary>
    public static string SourceOf(Caller caller) =>
        caller.Kind == CallerKind.User ? SourceUserPrefix + caller.Id : ActorNames.ClientPrefix + caller.Id;

    /// <summary>
    /// T-1212b (dashboard-api-m12 6.7 a): the row fingerprints a save of <paramref name="payload"/> would store, read
    /// the same way, so an upload's rows are compared with saved ones on equal terms.
    /// </summary>
    public IReadOnlySet<string> RowFingerprintsOf(JsonElement payload, DateTime nowUtc) =>
        Fingerprints(SavedPayloadReader.Read(payload), nowUtc);

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
