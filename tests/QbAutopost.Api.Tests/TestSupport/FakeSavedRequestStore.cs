using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="ISavedRequestStore"/> for tests: one lock around all state, ids from 1. It must pass the same
/// <c>SavedRequestStoreContract</c> as the EF Core store. Payloads and JSON nodes are deep-copied in and out, so a
/// caller can never change stored data through a returned object.
/// </summary>
public sealed class FakeSavedRequestStore : ISavedRequestStore
{
    private const string RestartError = "the service restarted while this post was running";

    private readonly object _gate = new();
    private readonly Dictionary<long, SavedRequest> _requests = new();
    private readonly Dictionary<long, List<SavedRequestVersion>> _versions = new();
    private readonly Dictionary<long, List<PostAttempt>> _attempts = new();
    private readonly Dictionary<long, List<SavedRequestEvent>> _events = new();
    private readonly Dictionary<long, IReadOnlySet<string>> _fingerprints = new();
    private long _nextRequestId = 1;
    private long _nextVersionId = 1;
    private long _nextAttemptId = 1;
    private long _nextEventId = 1;

    public Task<CreateResult> CreateAsync(NewSavedRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (request.IdempotencyKey is not null)
            {
                var existing = _requests.Values.FirstOrDefault(r =>
                    r.Source == request.Source && r.IdempotencyKey == request.IdempotencyKey);
                if (existing is not null)
                {
                    var kind = string.Equals(existing.OriginalBody, request.OriginalBody, StringComparison.Ordinal)
                        ? CreateKind.Replayed
                        : CreateKind.KeyReused;
                    return Task.FromResult(new CreateResult(kind, View(existing)));
                }
            }

            var id = _nextRequestId++;
            var payload = request.Payload.Clone();
            var record = new SavedRequest(
                id, SavedRequestStatus.Pending, request.Source,
                request.Read.Request?.Reference, request.Read.Request?.ControlTotal, request.Read.Request?.Transactions.Count ?? 0,
                1, request.OriginalBody, Sha256(request.OriginalBody), request.CanonicalSha256, request.IdempotencyKey,
                NeedsAttention(request.Read, request.Snapshot),
                request.Duplicate.Of, request.Duplicate.Reasons.ToList(), null, null,
                request.Snapshot, request.Snapshot.ValidatedUtc, null, null,
                request.NowUtc, request.CreatedBy, request.NowUtc, request.CreatedBy, 1, payload, null);
            _requests[id] = record;
            _versions[id] =
            [
                new SavedRequestVersion(
                    _nextVersionId++, id, 1, payload, request.Read.Readable, request.Read.Errors.ToList(), [], null,
                    request.NowUtc, request.CreatedBy),
            ];
            _fingerprints[id] = request.RowFingerprints.ToHashSet(StringComparer.Ordinal);
            _attempts[id] = [];
            _events[id] = [];
            AddEvent(id, SavedRequestEvents.Created, null, request.NowUtc, request.CreatedBy);
            if (request.Duplicate.Flagged)
            {
                AddEvent(
                    id, SavedRequestEvents.FlaggedDuplicate,
                    new JsonObject { ["of"] = request.Duplicate.Of, ["reasons"] = ToArray(request.Duplicate.Reasons) },
                    request.NowUtc, request.CreatedBy);
            }

            return Task.FromResult(new CreateResult(CreateKind.Created, View(record)));
        }
    }

    public Task<SavedRequest?> GetAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_requests.TryGetValue(id, out var r) ? View(r) : null);
        }
    }

    public Task<PagedResult<SavedRequestSummary>> ListAsync(SavedRequestQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            IEnumerable<SavedRequest> rows = _requests.Values;
            if (query.Statuses.Count > 0) rows = rows.Where(r => query.Statuses.Contains(r.Status));
            if (query.NeedsAttention is { } na) rows = rows.Where(r => r.NeedsAttention == na);
            if (query.Duplicate is { } dup) rows = rows.Where(r => (r.DuplicateReasons.Count > 0) == dup);
            if (query.Source is { Length: > 0 } source) rows = rows.Where(r => r.Source == source);
            if (query.CreatedFrom is { } from) rows = rows.Where(r => DateOnly.FromDateTime(r.CreatedUtc) >= from);
            if (query.CreatedTo is { } to) rows = rows.Where(r => DateOnly.FromDateTime(r.CreatedUtc) <= to);
            if (!string.IsNullOrWhiteSpace(query.Q)) rows = rows.Where(r => Matches(r, query.Q.Trim()));

            var ordered = Order(rows, query.Sort).ToList();
            var size = Math.Max(1, query.PageSize);
            var page = Math.Max(1, query.Page);
            var items = ordered.Skip((page - 1) * size).Take(size).Select(Summarize).ToList();
            return Task.FromResult(new PagedResult<SavedRequestSummary>(items, page, size, ordered.Count));
        }
    }

    public Task<SavedRequestStats> StatsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var byStatus = _requests.Values.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
            return Task.FromResult(new SavedRequestStats(
                _requests.Count, byStatus,
                _requests.Values.Count(r => r.NeedsAttention),
                _requests.Values.Count(r => r.DuplicateReasons.Count > 0)));
        }
    }

    public Task<IReadOnlyList<DuplicateDigest>> DuplicateCandidatesAsync(DateTime sinceUtc, long? excludeId, CancellationToken ct)
    {
        lock (_gate)
        {
            var digests = _requests.Values
                .Where(r => r.CreatedUtc >= sinceUtc && r.Status != SavedRequestStatus.Discarded && r.Id != excludeId)
                .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id)
                .Select(r => new DuplicateDigest(r.Id, r.CanonicalSha256, r.Reference, new HashSet<string>(_fingerprints[r.Id], StringComparer.Ordinal), r.CreatedUtc))
                .ToList();
            return Task.FromResult<IReadOnlyList<DuplicateDigest>>(digests);
        }
    }

    public Task<IReadOnlyList<SavedRequestVersion>> VersionsAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<SavedRequestVersion> list = _versions.TryGetValue(id, out var v)
                ? v.OrderBy(x => x.Number).Select(Copy).ToList()
                : [];
            return Task.FromResult(list);
        }
    }

    public Task<SavedRequestVersion?> VersionAsync(long id, int number, CancellationToken ct)
    {
        lock (_gate)
        {
            var found = _versions.TryGetValue(id, out var v) ? v.FirstOrDefault(x => x.Number == number) : null;
            return Task.FromResult(found is null ? null : Copy(found));
        }
    }

    public Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<PostAttempt> list = _attempts.TryGetValue(id, out var a)
                ? a.OrderBy(x => x.Id).Select(Copy).ToList()
                : [];
            return Task.FromResult(list);
        }
    }

    public Task<PostAttempt?> AttemptAsync(long id, long attemptId, CancellationToken ct)
    {
        lock (_gate)
        {
            var found = _attempts.TryGetValue(id, out var a) ? a.FirstOrDefault(x => x.Id == attemptId) : null;
            return Task.FromResult(found is null ? null : Copy(found));
        }
    }

    public Task<IReadOnlyList<SavedRequestEvent>> EventsAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<SavedRequestEvent> list = _events.TryGetValue(id, out var e)
                ? e.OrderBy(x => x.Id).Select(Copy).ToList()
                : [];
            return Task.FromResult(list);
        }
    }

    public Task<MutationResult> AppendVersionAsync(long id, int expectedRowVersion, NewVersion version, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r)) return Mutation(MutationKind.NotFound, null);
            if (r.RowVersion != expectedRowVersion) return Mutation(MutationKind.Stale, r);
            if (r.Status != SavedRequestStatus.Pending) return Mutation(MutationKind.WrongStatus, r);
            if (r.CanonicalSha256 == version.CanonicalSha256) return Mutation(MutationKind.Unchanged, r);

            var number = r.CurrentVersion + 1;
            var payload = version.Payload.Clone();
            _fingerprints[id] = version.RowFingerprints.ToHashSet(StringComparer.Ordinal);
            _versions[id].Add(new SavedRequestVersion(
                _nextVersionId++, id, number, payload, version.Read.Readable, version.Read.Errors.ToList(),
                version.Changes.Select(Copy).ToList(), version.Note, version.NowUtc, version.Actor));
            var updated = r with
            {
                CurrentVersion = number,
                Reference = version.Read.Request?.Reference,
                ControlTotal = version.Read.Request?.ControlTotal,
                TransactionCount = version.Read.Request?.Transactions.Count ?? 0,
                CanonicalSha256 = version.CanonicalSha256,
                NeedsAttention = NeedsAttention(version.Read, version.Snapshot),
                DuplicateOfId = version.Duplicate.Of,
                DuplicateReasons = version.Duplicate.Reasons.ToList(),
                Validation = version.Snapshot,
                ValidatedUtc = version.Snapshot.ValidatedUtc,
                CurrentPayload = payload,
                UpdatedUtc = version.NowUtc,
                UpdatedBy = version.Actor,
                RowVersion = r.RowVersion + 1,
            };
            _requests[id] = updated;
            AddEvent(
                id, SavedRequestEvents.Edited,
                new JsonObject
                {
                    ["version"] = number,
                    ["note"] = version.Note,
                    ["changes"] = new JsonArray(version.Changes.Select(c => (JsonNode)new JsonObject
                    {
                        ["op"] = c.Op, ["path"] = c.Path, ["old"] = c.Old?.DeepClone(), ["new"] = c.New?.DeepClone(),
                    }).ToArray()),
                },
                version.NowUtc, version.Actor);
            return Mutation(MutationKind.Ok, updated);
        }
    }

    public Task<MutationResult> SetValidationAsync(
        long id, ValidationSnapshot snapshot, DuplicateMatch duplicate, string actor, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r)) return Mutation(MutationKind.NotFound, null);
            if (r.Status != SavedRequestStatus.Pending) return Mutation(MutationKind.WrongStatus, r);
            if (snapshot.VersionNumber != r.CurrentVersion) return Mutation(MutationKind.Stale, r);

            var readable = _versions[id].Single(v => v.Number == r.CurrentVersion).Readable;
            var updated = r with
            {
                RowVersion = r.RowVersion + 1,
                Validation = snapshot,
                ValidatedUtc = snapshot.ValidatedUtc,
                NeedsAttention = !readable || snapshot.NeedsAttention,
                DuplicateOfId = duplicate.Of,
                DuplicateReasons = duplicate.Reasons.ToList(),
                UpdatedUtc = nowUtc,
                UpdatedBy = actor,
            };
            _requests[id] = updated;
            AddEvent(
                id, SavedRequestEvents.Validated,
                new JsonObject { ["version"] = snapshot.VersionNumber, ["state"] = snapshot.State.ToString() },
                nowUtc, actor);
            return Mutation(MutationKind.Ok, updated);
        }
    }

    public Task<MutationResult> ChangeStatusAsync(
        long id, int? expectedRowVersion, SavedRequestStatus from, SavedRequestStatus to, string actor,
        DateTime nowUtc, string eventKind, JsonNode? detail, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r)) return Mutation(MutationKind.NotFound, null);
            if (expectedRowVersion is { } expected && r.RowVersion != expected) return Mutation(MutationKind.Stale, r);
            if (r.Status != from) return Mutation(MutationKind.WrongStatus, r);

            var reason = to == SavedRequestStatus.Discarded && detail is JsonObject o && o["reason"] is JsonValue v && v.TryGetValue<string>(out var s)
                ? s
                : null;
            var updated = r with
            {
                Status = to,
                DiscardReason = to == SavedRequestStatus.Discarded ? reason : null,
                UpdatedUtc = nowUtc,
                UpdatedBy = actor,
                RowVersion = r.RowVersion + 1,
            };
            _requests[id] = updated;
            AddEvent(id, eventKind, detail?.DeepClone(), nowUtc, actor);
            return Mutation(MutationKind.Ok, updated);
        }
    }

    public Task<ClaimResult> TryClaimAsync(
        long id, int? expectedRowVersion, string actor, bool acknowledgeDuplicate, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r))
                return Task.FromResult(new ClaimResult(ClaimKind.NotFound, null, 0, null));

            switch (r.Status)
            {
                case SavedRequestStatus.Posting:
                    var running = _attempts[id].LastOrDefault(a => a.Outcome == AttemptOutcome.InProgress);
                    return Task.FromResult(new ClaimResult(
                        ClaimKind.AlreadyPosting, running?.Id, running?.VersionNumber ?? r.CurrentVersion, View(r)));
                case SavedRequestStatus.Posted:
                    return Task.FromResult(new ClaimResult(ClaimKind.AlreadyPosted, null, r.CurrentVersion, View(r)));
                case not SavedRequestStatus.Pending:
                    return Task.FromResult(new ClaimResult(ClaimKind.NotPostable, null, r.CurrentVersion, View(r)));
            }

            if (expectedRowVersion is { } expected && r.RowVersion != expected)
                return Task.FromResult(new ClaimResult(ClaimKind.Stale, null, r.CurrentVersion, View(r)));

            var attempt = new PostAttempt(
                _nextAttemptId++, id, r.CurrentVersion, AttemptOutcome.InProgress, null, null, null, null, null,
                nowUtc, null, actor, null, null, null);
            _attempts[id].Add(attempt);
            var updated = r with
            {
                Status = SavedRequestStatus.Posting,
                DuplicateAcknowledgedBy = acknowledgeDuplicate ? actor : r.DuplicateAcknowledgedBy,
                DuplicateAcknowledgedUtc = acknowledgeDuplicate ? nowUtc : r.DuplicateAcknowledgedUtc,
                UpdatedUtc = nowUtc,
                UpdatedBy = actor,
                RowVersion = r.RowVersion + 1,
            };
            _requests[id] = updated;
            AddEvent(
                id, SavedRequestEvents.PostStarted,
                new JsonObject { ["attemptId"] = attempt.Id, ["version"] = attempt.VersionNumber },
                nowUtc, actor);
            return Task.FromResult(new ClaimResult(ClaimKind.Claimed, attempt.Id, attempt.VersionNumber, View(updated)));
        }
    }

    public Task CompleteAttemptAsync(long attemptId, AttemptCompletion completion, CancellationToken ct)
    {
        lock (_gate)
        {
            var (requestId, index) = FindAttempt(attemptId);
            if (requestId is null || _attempts[requestId.Value][index].Outcome != AttemptOutcome.InProgress)
                throw new InvalidOperationException($"Attempt {attemptId} is unknown or no longer in progress.");

            var r = _requests[requestId.Value];
            if (r.Status != SavedRequestStatus.Posting)
                throw new InvalidOperationException($"Request {r.Id} is {r.Status}, not posting.");

            var attempt = _attempts[r.Id][index];
            _attempts[r.Id][index] = attempt with
            {
                Outcome = completion.Outcome,
                HttpStatus = completion.HttpStatus,
                PostId = completion.PostId,
                Result = completion.Result?.DeepClone(),
                Counts = completion.Counts?.DeepClone(),
                Error = completion.Error,
                FinishedUtc = completion.FinishedUtc,
            };
            _requests[r.Id] = r with
            {
                Status = completion.NextStatus,
                PostId = completion.PostId ?? r.PostId,
                UpdatedUtc = completion.FinishedUtc,
                UpdatedBy = attempt.StartedBy,
                RowVersion = r.RowVersion + 1,
            };
            AddEvent(
                r.Id, SavedRequestEvents.PostFinished,
                new JsonObject
                {
                    ["attemptId"] = attemptId,
                    ["outcome"] = completion.Outcome.ToString(),
                    ["status"] = completion.NextStatus.ToString(),
                    ["postId"] = completion.PostId,
                },
                completion.FinishedUtc, attempt.StartedBy);
            return Task.CompletedTask;
        }
    }

    public Task<MutationResult> ResolveAsync(
        long id, ResolveOutcome outcome, string note, string actor, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r)) return Mutation(MutationKind.NotFound, null);
            if (!SavedRequestRules.CanResolveTo(r.Status, outcome)) return Mutation(MutationKind.WrongStatus, r);

            var attempts = _attempts[id];
            var index = attempts.FindLastIndex(a => a.Outcome is AttemptOutcome.Unknown or AttemptOutcome.Partial);
            string? postId = null;
            if (index >= 0)
            {
                postId = attempts[index].PostId;
                attempts[index] = attempts[index] with { ResolvedBy = actor, ResolvedUtc = nowUtc, ResolutionNote = note };
            }

            var updated = r with
            {
                Status = outcome == ResolveOutcome.Posted ? SavedRequestStatus.Posted : SavedRequestStatus.Pending,
                PostId = outcome == ResolveOutcome.Posted ? postId ?? r.PostId : r.PostId,
                UpdatedUtc = nowUtc,
                UpdatedBy = actor,
                RowVersion = r.RowVersion + 1,
            };
            _requests[id] = updated;
            AddEvent(
                id, SavedRequestEvents.Resolved,
                new JsonObject { ["outcome"] = outcome.ToString(), ["note"] = note, ["from"] = r.Status.ToString() },
                nowUtc, actor);
            return Mutation(MutationKind.Ok, updated);
        }
    }

    public Task<int> RecoverInFlightAsync(DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var moved = 0;
            foreach (var r in _requests.Values.Where(r => r.Status == SavedRequestStatus.Posting).ToList())
            {
                var attempts = _attempts[r.Id];
                for (var i = 0; i < attempts.Count; i++)
                {
                    if (attempts[i].Outcome != AttemptOutcome.InProgress) continue;
                    attempts[i] = attempts[i] with
                    {
                        Outcome = AttemptOutcome.Unknown, Error = RestartError, FinishedUtc = nowUtc,
                    };
                }

                _requests[r.Id] = r with
                {
                    Status = SavedRequestStatus.Unknown, UpdatedUtc = nowUtc, UpdatedBy = "system", RowVersion = r.RowVersion + 1,
                };
                AddEvent(
                    r.Id, SavedRequestEvents.PostFinished,
                    new JsonObject { ["outcome"] = AttemptOutcome.Unknown.ToString(), ["error"] = RestartError },
                    nowUtc, "system");
                moved++;
            }

            return Task.FromResult(moved);
        }
    }

    // ---- helpers (call only inside the lock) --------------------------------------------------------------------

    private Task<MutationResult> Mutation(MutationKind kind, SavedRequest? record) =>
        Task.FromResult(new MutationResult(kind, record is null ? null : View(record)));

    private void AddEvent(long id, string kind, JsonNode? detail, DateTime at, string actor) =>
        _events[id].Add(new SavedRequestEvent(_nextEventId++, id, kind, detail, at, actor));

    private (long? RequestId, int Index) FindAttempt(long attemptId)
    {
        foreach (var (requestId, list) in _attempts)
        {
            var i = list.FindIndex(a => a.Id == attemptId);
            if (i >= 0) return (requestId, i);
        }

        return (null, -1);
    }

    private SavedRequest View(SavedRequest r)
    {
        var last = _attempts[r.Id].MaxBy(a => a.Id);
        return r with { LastAttempt = last is null ? null : Copy(last), DuplicateReasons = r.DuplicateReasons.ToList() };
    }

    private static bool NeedsAttention(PayloadRead read, ValidationSnapshot snapshot) => !read.Readable || snapshot.NeedsAttention;

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    private static IEnumerable<SavedRequest> Order(IEnumerable<SavedRequest> rows, string sort)
    {
        var descending = sort.StartsWith('-');
        var key = sort.TrimStart('-');
        return key switch
        {
            "created" => descending
                ? rows.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id),
            "updated" => descending
                ? rows.OrderByDescending(r => r.UpdatedUtc).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.UpdatedUtc).ThenBy(r => r.Id),
            // A request with no control total sorts last both ways (NULLS LAST), as the EF Core store does.
            "controlTotal" => descending
                ? rows.OrderBy(r => r.ControlTotal is null).ThenByDescending(r => r.ControlTotal).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.ControlTotal is null).ThenBy(r => r.ControlTotal).ThenBy(r => r.Id),
            _ => rows.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id),
        };
    }

    private static SavedRequestSummary Summarize(SavedRequest r) => new(
        r.Id, r.Status, r.Source, r.Reference, r.ControlTotal, r.TransactionCount, r.CurrentVersion, r.NeedsAttention,
        r.DuplicateOfId, r.DuplicateReasons.ToList(), r.Validation?.State, r.ValidatedUtc, r.PostId,
        r.CreatedUtc, r.CreatedBy, r.UpdatedUtc, r.UpdatedBy, r.RowVersion);

    private static bool Matches(SavedRequest r, string q)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        return (r.Reference?.Contains(q, ci) ?? false)
            || $"SR-{r.Id:000000}".Contains(q, ci)
            || PayloadText(r.CurrentPayload).Any(t => t.Contains(q, ci));
    }

    // Every string value of a "payee" or "memo" property, wherever it sits in the payload.
    private static IEnumerable<string> PayloadText(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String
                        && (string.Equals(p.Name, "payee", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Name, "memo", StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return p.Value.GetString()!;
                    }
                    else
                    {
                        foreach (var t in PayloadText(p.Value)) yield return t;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    foreach (var t in PayloadText(item)) yield return t;
                break;
        }
    }

    private static SavedRequestVersion Copy(SavedRequestVersion v) =>
        v with { Payload = v.Payload.Clone(), Changes = v.Changes.Select(Copy).ToList(), ReadErrors = v.ReadErrors.ToList() };

    private static PayloadChange Copy(PayloadChange c) => c with { Old = c.Old?.DeepClone(), New = c.New?.DeepClone() };

    private static PostAttempt Copy(PostAttempt a) => a with { Result = a.Result?.DeepClone(), Counts = a.Counts?.DeepClone() };

    private static SavedRequestEvent Copy(SavedRequestEvent e) => e with { Detail = e.Detail?.DeepClone() };
}
