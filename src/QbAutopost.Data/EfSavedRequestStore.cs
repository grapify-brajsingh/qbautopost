using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Text;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>
/// <see cref="ISavedRequestStore"/> on PostgreSQL (spec saved-requests-postgres §5, §9, §10). A singleton: every call
/// makes its own context from the pooled factory. Every mutation is one transaction that first locks the request row
/// (<c>FOR UPDATE</c>), so its checks and its writes see the same row; the claim is the single conditional update of
/// §10.2 instead. Nothing here updates or deletes a version or an event.
/// </summary>
public sealed partial class EfSavedRequestStore(IDbContextFactory<QbAutopostDbContext> factory) : ISavedRequestStore
{
    private const string RestartError = "the service restarted while this post was running";
    private const string SystemActor = "system";
    private const string IdempotencyIndex = "ix_saved_requests_source_idempotency_key";
    private const string PushIdIndex = "ux_saved_requests_push_id";
    private const string LikeEscape = "\\";

    // A claim whose update matched nothing but which then finds the request pending again (claimed and finished in
    // between) tries again, at most this many times in all, before it reports stale.
    private const int ClaimTries = 3;

    // jsonb columns: the app's one JSON configuration (camelCase, enums as strings), without indentation.
    private static readonly JsonSerializerOptions Json = new(JsonOptions.Default) { WriteIndented = false };

    // ---- create ------------------------------------------------------------------------------------------------

    public async Task<CreateResult> CreateAsync(NewSavedRequest request, CancellationToken ct)
    {
        if (request.IdempotencyKey is not null && await FindByKeyAsync(request.Source, request.IdempotencyKey, ct) is { } existing)
        {
            return Replay(existing, request);
        }

        // The key decides alone when it matched; the pushId check runs only when no key matched (spec 5.2).
        if (request.Metadata.PushId is { } pushId && await FindByPushIdAsync(pushId, ct) is { } held)
        {
            return PushReplay(held, request);
        }

        try
        {
            return await InsertAsync(request, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdempotencyIndex,
                                           })
        {
            // Another save with the same source and key committed between the look-up and the insert.
            var winner = await FindByKeyAsync(request.Source, request.IdempotencyKey!, ct)
                ?? throw new InvalidOperationException("The idempotency key conflicted but no request holds it.", ex);
            return Replay(winner, request);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PushIdIndex,
                                           })
        {
            // Another save with the same pushId committed between the look-up and the insert.
            var winner = await FindByPushIdAsync(request.Metadata.PushId!.Value, ct)
                ?? throw new InvalidOperationException("The pushId conflicted but no request holds it.", ex);
            return PushReplay(winner, request);
        }
    }

    private async Task<CreateResult> InsertAsync(NewSavedRequest request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var entity = NewRequestEntity(request, currentVersion: 1);
        entity.Versions.Add(NewVersionEntity(1, request.Payload, request.Read, "[]", null, request));
        entity.Events.Add(Event(SavedRequestEvents.Created, null, request.NowUtc, request.CreatedBy));
        db.SavedRequests.Add(entity);
        await db.SaveChangesAsync(ct);
        await FlagDuplicateAsync(db, entity.Id, request, ct);

        await tx.CommitAsync(ct);
        return new CreateResult(CreateKind.Created, (await LoadAsync(db, entity.Id, ct))!);
    }

    /// <summary>The row of a new pending request at <paramref name="currentVersion"/>, without versions or events.</summary>
    private static SavedRequestEntity NewRequestEntity(NewSavedRequest request, int currentVersion)
    {
        var parsed = request.Read.Request;
        var entity = new SavedRequestEntity
        {
            Status = SavedRequestStatus.Pending,
            Source = request.Source,
            Reference = parsed?.Reference,
            ControlTotal = SavedRequestColumns.ControlTotalOf(request.Read),
            TransactionCount = parsed?.Transactions.Count ?? 0,
            CurrentVersion = currentVersion,
            OriginalBody = request.OriginalBody,
            OriginalSha256 = Sha256(request.OriginalBody),
            CanonicalSha256 = request.CanonicalSha256,
            IdempotencyKey = request.IdempotencyKey,
            NeedsAttention = NeedsAttention(request.Read, request.Snapshot),
            DuplicateOfId = request.Duplicate.Of,
            DuplicateReasons = request.Duplicate.Reasons.ToList(),
            Validation = JsonSerializer.Serialize(request.Snapshot, Json),
            ValidatedUtc = request.Snapshot.ValidatedUtc,
            RowFingerprints = request.RowFingerprints.ToList(),
            CreatedUtc = request.NowUtc,
            CreatedBy = request.CreatedBy,
            UpdatedUtc = request.NowUtc,
            UpdatedBy = request.CreatedBy,
            RowVersion = 1,
            EmailSubject = request.Metadata.EmailSubject,
            EmailReceivedUtc = request.Metadata.EmailReceivedUtc,
            EmailFrom = request.Metadata.EmailFrom,
            EmailMessageId = request.Metadata.EmailMessageId,
            PushId = request.Metadata.PushId,
            MetadataErrors = request.Metadata.MetadataErrors.ToList(),
            SkipCount = SavedRequestColumns.SkipCountOf(request.Payload),
        };
        SetSnapshotV2(entity, request.Read.Readable, request.SnapshotV2 ?? request.Snapshot);
        return entity;
    }

    /// <summary>A version written by a save: by the save's author, at the save's time.</summary>
    private static SavedRequestVersionEntity NewVersionEntity(
        int number, JsonElement payload, PayloadRead read, string changes, string? note, NewSavedRequest request) => new()
    {
        Number = number,
        Payload = payload.GetRawText(),
        Readable = read.Readable,
        ReadErrors = read.Errors.ToList(),
        Changes = changes,
        Note = note,
        CreatedUtc = request.NowUtc,
        CreatedBy = request.CreatedBy,
    };

    // A second save, so the flag's id comes after the created event's and the timeline reads in order.
    private static async Task FlagDuplicateAsync(QbAutopostDbContext db, long id, NewSavedRequest request, CancellationToken ct)
    {
        if (request.Duplicate.Flagged)
        {
            AddEvent(
                db, id, SavedRequestEvents.FlaggedDuplicate,
                new JsonObject { ["of"] = request.Duplicate.Of, ["reasons"] = ToArray(request.Duplicate.Reasons) },
                request.NowUtc, request.CreatedBy);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<SavedRequest?> FindByKeyAsync(string source, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = await db.SavedRequests
            .Where(r => r.Source == source && r.IdempotencyKey == key)
            .Select(r => (long?)r.Id)
            .SingleOrDefaultAsync(ct);
        return id is null ? null : await LoadAsync(db, id.Value, ct);
    }

    private async Task<SavedRequest?> FindByPushIdAsync(Guid pushId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = await db.SavedRequests
            .Where(r => r.PushId == pushId)
            .Select(r => (long?)r.Id)
            .SingleOrDefaultAsync(ct);
        return id is null ? null : await LoadAsync(db, id.Value, ct);
    }

    private static CreateResult PushReplay(SavedRequest existing, NewSavedRequest request) =>
        new(existing.OriginalSha256 == Sha256(request.OriginalBody) ? CreateKind.Replayed : CreateKind.PushIdReused, existing);

    private static CreateResult Replay(SavedRequest existing, NewSavedRequest request) =>
        new(existing.OriginalSha256 == Sha256(request.OriginalBody) ? CreateKind.Replayed : CreateKind.KeyReused, existing);

    // ---- reads -------------------------------------------------------------------------------------------------

    public async Task<SavedRequest?> GetAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await LoadAsync(db, id, ct);
    }

    public async Task<PagedResult<SavedRequestSummary>> ListAsync(SavedRequestQuery query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = FilterRequests(db, query, v2: false);

        var total = await rows.CountAsync(ct);
        var size = Math.Max(1, query.PageSize);
        var page = Math.Max(1, query.Page);
        var items = await Order(rows, query.Sort)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new
            {
                r.Id, r.Status, r.Source, r.Reference, r.ControlTotal, r.TransactionCount, r.CurrentVersion, r.NeedsAttention,
                r.DuplicateOfId, r.DuplicateReasons, r.Validation, r.ValidatedUtc, r.PostId, r.CreatedUtc, r.CreatedBy,
                r.UpdatedUtc, r.UpdatedBy, r.RowVersion,
            })
            .ToListAsync(ct);

        var summaries = items.Select(r => new SavedRequestSummary(
            r.Id, r.Status, r.Source, r.Reference, r.ControlTotal, r.TransactionCount, r.CurrentVersion, r.NeedsAttention,
            r.DuplicateOfId, r.DuplicateReasons.ToList(), ReadSnapshot(r.Validation)?.State, r.ValidatedUtc, r.PostId,
            r.CreatedUtc, r.CreatedBy, r.UpdatedUtc, r.UpdatedBy, r.RowVersion)).ToList();
        return new PagedResult<SavedRequestSummary>(summaries, page, size, total);
    }

    public async Task<SavedRequestStats> StatsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var byStatus = await db.SavedRequests
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
        var needsAttention = await db.SavedRequests.CountAsync(r => r.NeedsAttention, ct);
        var duplicates = await db.SavedRequests.CountAsync(r => r.DuplicateReasons.Count > 0, ct);
        return new SavedRequestStats(byStatus.Values.Sum(), byStatus, needsAttention, duplicates);
    }

    public async Task<IReadOnlyList<DuplicateDigest>> DuplicateCandidatesAsync(DateTime sinceUtc, long? excludeId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = db.SavedRequests.Where(r => r.CreatedUtc >= sinceUtc && r.Status != SavedRequestStatus.Discarded);
        if (excludeId is { } excluded) rows = rows.Where(r => r.Id != excluded);

        var digests = await rows
            .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.CanonicalSha256, r.Reference, r.RowFingerprints, r.CreatedUtc })
            .ToListAsync(ct);
        return digests
            .Select(d => new DuplicateDigest(
                d.Id, d.CanonicalSha256, d.Reference, new HashSet<string>(d.RowFingerprints, StringComparer.Ordinal), d.CreatedUtc))
            .ToList();
    }

    public async Task<IReadOnlyList<SavedRequestVersion>> VersionsAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var versions = await db.SavedRequestVersions.AsNoTracking()
            .Where(v => v.SavedRequestId == id).OrderBy(v => v.Number).ToListAsync(ct);
        return versions.Select(ToModel).ToList();
    }

    public async Task<SavedRequestVersion?> VersionAsync(long id, int number, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var version = await db.SavedRequestVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.SavedRequestId == id && v.Number == number, ct);
        return version is null ? null : ToModel(version);
    }

    public async Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var attempts = await db.PostAttempts.AsNoTracking()
            .Where(a => a.SavedRequestId == id).OrderBy(a => a.Id).ToListAsync(ct);
        return attempts.Select(ToModel).ToList();
    }

    public async Task<PostAttempt?> AttemptAsync(long id, long attemptId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var attempt = await db.PostAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == attemptId && a.SavedRequestId == id, ct);
        return attempt is null ? null : ToModel(attempt);
    }

    public async Task<IReadOnlyList<SavedRequestEvent>> EventsAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var events = await db.SavedRequestEvents.AsNoTracking()
            .Where(e => e.SavedRequestId == id).OrderBy(e => e.Id).ToListAsync(ct);
        return events.Select(e => new SavedRequestEvent(e.Id, e.SavedRequestId, e.Kind, ParseNode(e.Detail), e.AtUtc, e.Actor)).ToList();
    }

    // ---- mutations ---------------------------------------------------------------------------------------------

    public Task<MutationResult> AppendVersionAsync(long id, int expectedRowVersion, NewVersion version, CancellationToken ct) =>
        MutateAsync(id, ct, async (db, r) =>
        {
            if (r.RowVersion != expectedRowVersion) return MutationKind.Stale;
            if (r.Status != SavedRequestStatus.Pending) return MutationKind.WrongStatus;
            if (r.CanonicalSha256 == version.CanonicalSha256) return MutationKind.Unchanged;

            var number = r.CurrentVersion + 1;
            db.SavedRequestVersions.Add(new SavedRequestVersionEntity
            {
                SavedRequestId = id,
                Number = number,
                Payload = version.Payload.GetRawText(),
                Readable = version.Read.Readable,
                ReadErrors = version.Read.Errors.ToList(),
                Changes = JsonSerializer.Serialize(version.Changes, Json),
                Note = version.Note,
                CreatedUtc = version.NowUtc,
                CreatedBy = version.Actor,
            });

            var parsed = version.Read.Request;
            r.CurrentVersion = number;
            r.Reference = parsed?.Reference;
            r.ControlTotal = SavedRequestColumns.ControlTotalOf(version.Read);
            r.TransactionCount = parsed?.Transactions.Count ?? 0;
            r.CanonicalSha256 = version.CanonicalSha256;
            r.NeedsAttention = NeedsAttention(version.Read, version.Snapshot);
            r.DuplicateOfId = version.Duplicate.Of;
            r.DuplicateReasons = version.Duplicate.Reasons.ToList();
            r.Validation = JsonSerializer.Serialize(version.Snapshot, Json);
            r.ValidatedUtc = version.Snapshot.ValidatedUtc;
            r.RowFingerprints = version.RowFingerprints.ToList();
            r.SkipCount = SavedRequestColumns.SkipCountOf(version.Payload);
            SetSnapshotV2(r, version.Read.Readable, version.SnapshotV2 ?? version.Snapshot);
            Touch(r, version.Actor, version.NowUtc);
            AddEvent(
                db, id, SavedRequestEvents.Edited,
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
            await db.SaveChangesAsync(ct);
            return MutationKind.Ok;
        });

    public Task<MutationResult> SetValidationAsync(
        long id, ValidationSnapshot snapshot, ValidationSnapshot snapshotV2, DuplicateMatch duplicate, string actor,
        DateTime nowUtc, CancellationToken ct) =>
        MutateAsync(id, ct, async (db, r) =>
        {
            if (r.Status != SavedRequestStatus.Pending) return MutationKind.WrongStatus;
            if (snapshot.VersionNumber != r.CurrentVersion) return MutationKind.Stale;
            if (snapshotV2.VersionNumber != r.CurrentVersion) return MutationKind.Stale;

            var readable = await db.SavedRequestVersions
                .Where(v => v.SavedRequestId == id && v.Number == r.CurrentVersion)
                .Select(v => v.Readable)
                .SingleAsync(ct);
            r.Validation = JsonSerializer.Serialize(snapshot, Json);
            r.ValidatedUtc = snapshot.ValidatedUtc;
            r.NeedsAttention = !readable || snapshot.NeedsAttention;
            SetSnapshotV2(r, readable, snapshotV2);
            r.DuplicateOfId = duplicate.Of;
            r.DuplicateReasons = duplicate.Reasons.ToList();
            Touch(r, actor, nowUtc);
            AddEvent(
                db, id, SavedRequestEvents.Validated,
                new JsonObject { ["version"] = snapshot.VersionNumber, ["state"] = snapshot.State.ToString() },
                nowUtc, actor);
            await db.SaveChangesAsync(ct);
            return MutationKind.Ok;
        });

    public Task<MutationResult> ChangeStatusAsync(
        long id, int? expectedRowVersion, SavedRequestStatus from, SavedRequestStatus to, string actor,
        DateTime nowUtc, string eventKind, JsonNode? detail, CancellationToken ct) =>
        MutateAsync(id, ct, async (db, r) =>
        {
            if (expectedRowVersion is { } expected && r.RowVersion != expected) return MutationKind.Stale;
            if (r.Status != from) return MutationKind.WrongStatus;

            var reason = to == SavedRequestStatus.Discarded
                         && detail is JsonObject o && o["reason"] is JsonValue v && v.TryGetValue<string>(out var s)
                ? s
                : null;
            r.Status = to;
            r.DiscardReason = to == SavedRequestStatus.Discarded ? reason : null;
            Touch(r, actor, nowUtc);
            AddEvent(db, id, eventKind, detail?.DeepClone(), nowUtc, actor);
            await db.SaveChangesAsync(ct);
            return MutationKind.Ok;
        });

    public async Task<ClaimResult> TryClaimAsync(
        long id, int? expectedRowVersion, string actor, bool acknowledgeDuplicate, DateTime nowUtc, CancellationToken ct)
    {
        for (var tries = 1; ; tries++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var pending = db.SavedRequests.Where(r => r.Id == id && r.Status == SavedRequestStatus.Pending);
                if (expectedRowVersion is { } expected) pending = pending.Where(r => r.RowVersion == expected);

                // §10.2: the one conditional update. A racing caller waits on the row lock, then matches nothing.
                var claimed = await pending.ExecuteUpdateAsync(
                    set => set
                        .SetProperty(r => r.Status, SavedRequestStatus.Posting)
                        .SetProperty(r => r.RowVersion, r => r.RowVersion + 1)
                        .SetProperty(r => r.UpdatedBy, actor)
                        .SetProperty(r => r.UpdatedUtc, nowUtc)
                        .SetProperty(r => r.DuplicateAcknowledgedBy, r => acknowledgeDuplicate ? actor : r.DuplicateAcknowledgedBy)
                        .SetProperty(r => r.DuplicateAcknowledgedUtc, r => acknowledgeDuplicate ? nowUtc : r.DuplicateAcknowledgedUtc),
                    ct);

                if (claimed == 1)
                {
                    var versionNumber = await db.SavedRequests.Where(r => r.Id == id).Select(r => r.CurrentVersion).SingleAsync(ct);
                    var attempt = new PostAttemptEntity
                    {
                        SavedRequestId = id,
                        VersionNumber = versionNumber,
                        Outcome = AttemptOutcome.InProgress,
                        StartedUtc = nowUtc,
                        StartedBy = actor,
                    };
                    db.PostAttempts.Add(attempt);
                    await db.SaveChangesAsync(ct);
                    AddEvent(
                        db, id, SavedRequestEvents.PostStarted,
                        new JsonObject { ["attemptId"] = attempt.Id, ["version"] = versionNumber }, nowUtc, actor);
                    await db.SaveChangesAsync(ct);
                    // The record is read inside the claim's transaction: if the read fails the claim rolls back and the
                    // request is pending again, rather than committed as posting with nothing running for it.
                    var record = await LoadAsync(db, id, ct)
                                 ?? throw new InvalidOperationException($"Saved request {id} vanished inside its own claim.");
                    await tx.CommitAsync(ct);
                    return new ClaimResult(ClaimKind.Claimed, attempt.Id, versionNumber, record);
                }

                await tx.RollbackAsync(ct);
            }

            // Nothing matched: read the request to say why, in the fake's order (status first, then row version).
            var current = await LoadAsync(db, id, ct);
            switch (current?.Status)
            {
                case null:
                    return new ClaimResult(ClaimKind.NotFound, null, 0, null);
                case SavedRequestStatus.Posting:
                    var running = await db.PostAttempts.AsNoTracking()
                        .Where(a => a.SavedRequestId == id && a.Outcome == AttemptOutcome.InProgress)
                        .OrderByDescending(a => a.Id)
                        .FirstOrDefaultAsync(ct);
                    return new ClaimResult(
                        ClaimKind.AlreadyPosting, running?.Id, running?.VersionNumber ?? current.CurrentVersion, current);
                case SavedRequestStatus.Posted:
                    return new ClaimResult(ClaimKind.AlreadyPosted, null, current.CurrentVersion, current);
                case not SavedRequestStatus.Pending:
                    return new ClaimResult(ClaimKind.NotPostable, null, current.CurrentVersion, current);
            }

            // Pending: the expected row version is old, or the request was claimed and finished in between.
            if (expectedRowVersion is not null || tries == ClaimTries)
            {
                return new ClaimResult(ClaimKind.Stale, null, current.CurrentVersion, current);
            }
        }
    }

    public async Task CompleteAttemptAsync(long attemptId, AttemptCompletion completion, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock the request before the attempt, the order every other mutation uses, so two cannot deadlock.
        var requestId = await db.PostAttempts
            .Where(a => a.Id == attemptId)
            .Select(a => (long?)a.SavedRequestId)
            .SingleOrDefaultAsync(ct);
        var r = requestId is null ? null : await LockRequestAsync(db, requestId.Value, ct);
        var attempt = r is null
            ? null
            : (await db.PostAttempts.FromSql($"SELECT * FROM post_attempts WHERE id = {attemptId} FOR UPDATE").ToListAsync(ct))
                .SingleOrDefault();
        if (r is null || attempt is null || attempt.Outcome != AttemptOutcome.InProgress)
        {
            throw new InvalidOperationException($"Attempt {attemptId} is unknown or no longer in progress.");
        }

        if (r.Status != SavedRequestStatus.Posting)
        {
            throw new InvalidOperationException($"Request {r.Id} is {r.Status}, not posting.");
        }

        attempt.Outcome = completion.Outcome;
        attempt.HttpStatus = completion.HttpStatus;
        attempt.PostId = completion.PostId;
        attempt.Result = completion.Result?.ToJsonString();
        attempt.Counts = completion.Counts?.ToJsonString();
        attempt.Error = completion.Error;
        attempt.FinishedUtc = completion.FinishedUtc;

        r.Status = completion.NextStatus;
        r.PostId = completion.PostId ?? r.PostId;
        Touch(r, attempt.StartedBy, completion.FinishedUtc);
        AddEvent(
            db, r.Id, SavedRequestEvents.PostFinished,
            new JsonObject
            {
                ["attemptId"] = attemptId,
                ["outcome"] = completion.Outcome.ToString(),
                ["status"] = completion.NextStatus.ToString(),
                ["postId"] = completion.PostId,
            },
            completion.FinishedUtc, attempt.StartedBy);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task<MutationResult> ResolveAsync(
        long id, ResolveOutcome outcome, string note, string actor, DateTime nowUtc, CancellationToken ct) =>
        MutateAsync(id, ct, async (db, r) =>
        {
            if (!SavedRequestRules.CanResolveTo(r.Status, outcome)) return MutationKind.WrongStatus;

            var attempt = await db.PostAttempts
                .Where(a => a.SavedRequestId == id && (a.Outcome == AttemptOutcome.Unknown || a.Outcome == AttemptOutcome.Partial))
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (attempt is not null)
            {
                attempt.ResolvedBy = actor;
                attempt.ResolvedUtc = nowUtc;
                attempt.ResolutionNote = note;
            }

            var from = r.Status;
            r.Status = outcome == ResolveOutcome.Posted ? SavedRequestStatus.Posted : SavedRequestStatus.Pending;
            if (outcome == ResolveOutcome.Posted) r.PostId = attempt?.PostId ?? r.PostId;
            Touch(r, actor, nowUtc);
            AddEvent(
                db, id, SavedRequestEvents.Resolved,
                new JsonObject { ["outcome"] = outcome.ToString(), ["note"] = note, ["from"] = from.ToString() },
                nowUtc, actor);
            await db.SaveChangesAsync(ct);
            return MutationKind.Ok;
        });

    public async Task<int> RecoverInFlightAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var posting = KebabCaseEnumConverter<SavedRequestStatus>.ToText(SavedRequestStatus.Posting);
        var requests = await db.SavedRequests
            .FromSql($"SELECT * FROM saved_requests WHERE status = {posting} ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        var ids = requests.Select(r => r.Id).ToList();
        var attempts = await db.PostAttempts
            .Where(a => ids.Contains(a.SavedRequestId) && a.Outcome == AttemptOutcome.InProgress)
            .ToListAsync(ct);

        foreach (var attempt in attempts)
        {
            attempt.Outcome = AttemptOutcome.Unknown;
            attempt.Error = RestartError;
            attempt.FinishedUtc = nowUtc;
        }

        foreach (var r in requests)
        {
            r.Status = SavedRequestStatus.Unknown;
            Touch(r, SystemActor, nowUtc);
            AddEvent(
                db, r.Id, SavedRequestEvents.PostFinished,
                new JsonObject { ["outcome"] = AttemptOutcome.Unknown.ToString(), ["error"] = RestartError },
                nowUtc, SystemActor);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return requests.Count;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>
    /// One transaction: lock the request row, let <paramref name="work"/> check and change it, then return the
    /// request as it now reads (unchanged when the work refused).
    /// </summary>
    private async Task<MutationResult> MutateAsync(
        long id, CancellationToken ct, Func<QbAutopostDbContext, SavedRequestEntity, Task<MutationKind>> work)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var r = await LockRequestAsync(db, id, ct);
        if (r is null)
        {
            return new MutationResult(MutationKind.NotFound, null);
        }

        var kind = await work(db, r);
        await tx.CommitAsync(ct);
        return new MutationResult(kind, await LoadAsync(db, id, ct));
    }

    private static async Task<SavedRequestEntity?> LockRequestAsync(QbAutopostDbContext db, long id, CancellationToken ct) =>
        (await db.SavedRequests.FromSql($"SELECT * FROM saved_requests WHERE id = {id} FOR UPDATE").ToListAsync(ct))
            .SingleOrDefault();

    private static async Task<SavedRequest?> LoadAsync(QbAutopostDbContext db, long id, CancellationToken ct)
    {
        var r = await db.SavedRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return null;
        }

        var payload = await db.SavedRequestVersions
            .Where(v => v.SavedRequestId == id && v.Number == r.CurrentVersion)
            .Select(v => v.Payload)
            .SingleAsync(ct);
        var last = await db.PostAttempts.AsNoTracking()
            .Where(a => a.SavedRequestId == id)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        return new SavedRequest(
            r.Id, r.Status, r.Source, r.Reference, r.ControlTotal, r.TransactionCount, r.CurrentVersion,
            r.OriginalBody, r.OriginalSha256, r.CanonicalSha256, r.IdempotencyKey, r.NeedsAttention,
            r.DuplicateOfId, r.DuplicateReasons.ToList(), r.DuplicateAcknowledgedBy, r.DuplicateAcknowledgedUtc,
            ReadSnapshot(r.Validation), r.ValidatedUtc, r.PostId, r.DiscardReason,
            r.CreatedUtc, r.CreatedBy, r.UpdatedUtc, r.UpdatedBy, r.RowVersion,
            ParseElement(payload), last is null ? null : ToModel(last))
        {
            Metadata = new SaveMetadata(
                r.EmailSubject, r.EmailReceivedUtc, r.EmailFrom, r.EmailMessageId, r.PushId, r.MetadataErrors.ToList()),
            SkipCount = r.SkipCount,
            ValidationV2 = ReadSnapshot(r.ValidationV2),
            NeedsAttentionV2 = r.NeedsAttentionV2,
            ProblemCount = r.ProblemCount,
            Upload = UploadOf(r.UploadExtractionId, r.UploadFileName, r.UploadSizeBytes, r.UploadSha256, r.UploadModel,
                r.UploadPromptVersion, r.UploadExtractedUtc),
        };
    }

    /// <summary>
    /// The v1 list filters. With <paramref name="v2"/> (T-1107), <c>needsAttention</c> reads <c>needs_attention_v2</c>
    /// and <c>q</c> also matches the email subject, the push id prefix and (T-1210) the upload file name.
    /// </summary>
    private static IQueryable<SavedRequestEntity> FilterRequests(QbAutopostDbContext db, SavedRequestQuery query, bool v2)
    {
        IQueryable<SavedRequestEntity> rows = db.SavedRequests.AsNoTracking();

        if (query.Statuses.Count > 0)
        {
            var statuses = query.Statuses.ToList();
            rows = rows.Where(r => statuses.Contains(r.Status));
        }

        if (query.NeedsAttention is { } needsAttention)
        {
            rows = v2
                ? rows.Where(r => r.NeedsAttentionV2 == needsAttention)
                : rows.Where(r => r.NeedsAttention == needsAttention);
        }

        if (query.Duplicate is { } duplicate) rows = rows.Where(r => (r.DuplicateReasons.Count > 0) == duplicate);
        if (query.Source is { Length: > 0 } source) rows = rows.Where(r => r.Source == source);
        if (query.CreatedFrom is { } from)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(r => r.CreatedUtc >= fromUtc);
        }

        if (query.CreatedTo is { } to)
        {
            var untilUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(r => r.CreatedUtc < untilUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var q = query.Q.Trim();
            var pattern = "%" + EscapeLike(q) + "%";
            var payloadMatches = PayloadMatches(db, q);
            if (!v2)
            {
                rows = rows.Where(r =>
                    EF.Functions.ILike(r.Reference!, pattern, LikeEscape)
                    || EF.Functions.ILike(r.PublicRef, pattern, LikeEscape)
                    || payloadMatches.Contains(r.Id));
            }
            else if (SavedRequestSearch.PushIdHexPrefix(q) is { } hex)
            {
                // Hex digits need no LIKE escaping; the prefix is a parameter.
                var prefix = hex + "%";
                rows = rows.Where(r =>
                    EF.Functions.ILike(r.Reference!, pattern, LikeEscape)
                    || EF.Functions.ILike(r.PublicRef, pattern, LikeEscape)
                    || payloadMatches.Contains(r.Id)
                    || EF.Functions.ILike(r.EmailSubject!, pattern, LikeEscape)
                    || EF.Functions.ILike(r.UploadFileName!, pattern, LikeEscape)
                    || EF.Functions.Like(r.PushIdHex!, prefix));
            }
            else
            {
                rows = rows.Where(r =>
                    EF.Functions.ILike(r.Reference!, pattern, LikeEscape)
                    || EF.Functions.ILike(r.PublicRef, pattern, LikeEscape)
                    || payloadMatches.Contains(r.Id)
                    || EF.Functions.ILike(r.EmailSubject!, pattern, LikeEscape)
                    || EF.Functions.ILike(r.UploadFileName!, pattern, LikeEscape));
            }
        }

        return rows;
    }

    /// <summary>Ids of requests whose current payload has a <c>payee</c> or <c>memo</c> string containing <paramref name="q"/>, ignoring case.</summary>
    private static IQueryable<long> PayloadMatches(QbAutopostDbContext db, string q)
    {
        // like_regex takes only a literal pattern, so the whole jsonpath is the parameter. Flag "q" makes the pattern
        // a plain substring, so q needs escaping only as a jsonpath string.
        var path =
            """$.** ? (@.type() == "object").keyvalue() ? (@.key like_regex "^(payee|memo)$" flag "i" && @.value.type() == "string" && @.value like_regex """
            + JsonPathString(q)
            + """ flag "iq")""";
        return db.Database.SqlQuery<long>(
            $"""
            SELECT v.saved_request_id AS "Value"
            FROM saved_request_versions v
            JOIN saved_requests s ON s.id = v.saved_request_id AND s.current_version = v.number
            WHERE jsonb_path_exists(v.payload, {path}::jsonpath)
            """);
    }

    private static string JsonPathString(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string EscapeLike(string text) =>
        text.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);

    private static IQueryable<SavedRequestEntity> Order(IQueryable<SavedRequestEntity> rows, string sort)
    {
        var descending = sort.StartsWith('-');
        return sort.TrimStart('-') switch
        {
            "created" when !descending => rows.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id),
            "updated" => descending
                ? rows.OrderByDescending(r => r.UpdatedUtc).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.UpdatedUtc).ThenBy(r => r.Id),
            // No control total sorts last both ways (NULLS LAST), as in the fake.
            "controlTotal" => descending
                ? rows.OrderBy(r => r.ControlTotal == null).ThenByDescending(r => r.ControlTotal).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.ControlTotal == null).ThenBy(r => r.ControlTotal).ThenBy(r => r.Id),
            _ => rows.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id),
        };
    }

    private static void Touch(SavedRequestEntity r, string actor, DateTime nowUtc)
    {
        r.UpdatedUtc = nowUtc;
        r.UpdatedBy = actor;
        r.RowVersion++;
    }

    private static void AddEvent(QbAutopostDbContext db, long id, string kind, JsonNode? detail, DateTime at, string actor)
    {
        var e = Event(kind, detail, at, actor);
        e.SavedRequestId = id;
        db.SavedRequestEvents.Add(e);
    }

    private static SavedRequestEventEntity Event(string kind, JsonNode? detail, DateTime at, string actor) =>
        new() { Kind = kind, Detail = detail?.ToJsonString(), AtUtc = at, Actor = actor };

    private static SavedRequestVersion ToModel(SavedRequestVersionEntity v) => new(
        v.Id, v.SavedRequestId, v.Number, ParseElement(v.Payload), v.Readable, v.ReadErrors.ToList(),
        JsonSerializer.Deserialize<List<PayloadChange>>(v.Changes, Json) ?? [], v.Note, v.CreatedUtc, v.CreatedBy);

    private static PostAttempt ToModel(PostAttemptEntity a) => new(
        a.Id, a.SavedRequestId, a.VersionNumber, a.Outcome, a.HttpStatus, a.PostId, ParseNode(a.Result), ParseNode(a.Counts),
        a.Error, a.StartedUtc, a.FinishedUtc, a.StartedBy, a.ResolvedBy, a.ResolvedUtc, a.ResolutionNote);

    private static ValidationSnapshot? ReadSnapshot(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<ValidationSnapshot>(json, Json);

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonNode? ParseNode(string? json) => json is null ? null : JsonNode.Parse(json);

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    /// <summary>T-1105: the v2 snapshot and the two columns written with it. Metadata must already be on the row.</summary>
    private static void SetSnapshotV2(SavedRequestEntity r, bool readable, ValidationSnapshot snapshotV2)
    {
        r.ValidationV2 = JsonSerializer.Serialize(snapshotV2, Json);
        r.NeedsAttentionV2 = SavedRequestColumns.NeedsAttentionV2(
            readable, snapshotV2, SaveMetadata.None with { MetadataErrors = r.MetadataErrors });
        r.ProblemCount = snapshotV2.ProblemCount;
    }

    private static bool NeedsAttention(PayloadRead read, ValidationSnapshot snapshot) => !read.Readable || snapshot.NeedsAttention;

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
