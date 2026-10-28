using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>The extraction records (T-1209, dashboard-api-m12 6.6) and the save of a reviewed upload (T-1210, 7.2).</summary>
public sealed partial class EfSavedRequestStore : IUploadStore
{
    public async Task CreateExtractionAsync(StatementExtraction extraction, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.StatementExtractions.Add(new StatementExtractionEntity
        {
            Id = extraction.Id,
            UserId = extraction.UserId,
            FileName = extraction.FileName,
            SizeBytes = extraction.SizeBytes,
            Sha256 = extraction.Sha256,
            PageCount = extraction.PageCount,
            Model = extraction.Model,
            PromptVersion = extraction.PromptVersion,
            Payload = extraction.Payload.ToJsonString(),
            ExtractedUtc = extraction.ExtractedUtc,
            ExpiresUtc = extraction.ExpiresUtc,
            SavedRequestId = extraction.SavedRequestId,
        });
        await db.SaveChangesAsync(ct);
    }

    // One statement; unsaved only, so a record a request points at is never lost.
    public async Task<int> DeleteExpiredExtractionsAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.StatementExtractions
            .Where(e => e.SavedRequestId == null && e.ExpiresUtc <= nowUtc)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<StatementExtraction?> GetExtractionAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.StatementExtractions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null
            ? null
            : new StatementExtraction(
                e.Id, e.UserId, e.FileName, e.SizeBytes, e.Sha256, e.PageCount, e.Model, e.PromptVersion,
                JsonNode.Parse(e.Payload)!.AsObject(),
                DateTime.SpecifyKind(e.ExtractedUtc, DateTimeKind.Utc), DateTime.SpecifyKind(e.ExpiresUtc, DateTimeKind.Utc),
                e.SavedRequestId);
    }


    /// <summary>
    /// T-1210 (spec 7.2, 7.5): one transaction that first locks the extraction row <c>FOR UPDATE</c>, so a second save
    /// of the same extraction waits for the first and then sees its <c>saved_request_id</c>. The idempotency look-up
    /// runs right after the lock (and before every other check), so a same-key retry that waited still finds the
    /// request the first one committed. Only <c>Created</c> and <c>Expired</c> write.
    /// </summary>
    public async Task<UploadSaveResult> SaveUploadAsync(NewUploadSave save, CancellationToken ct)
    {
        try
        {
            return await TrySaveUploadAsync(save, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdempotencyIndex,
                                           })
        {
            // A save of another extraction with the same source and key committed between the look-up and the insert.
            var request = save.Request;
            var winner = await FindByKeyAsync(request.Source, request.IdempotencyKey!, ct)
                ?? throw new InvalidOperationException("The idempotency key conflicted but no request holds it.", ex);
            return Answer(
                winner.OriginalSha256 == Sha256(request.OriginalBody) ? UploadSaveKind.Replayed : UploadSaveKind.KeyReused,
                winner);
        }
    }

    private async Task<UploadSaveResult> TrySaveUploadAsync(NewUploadSave save, CancellationToken ct)
    {
        var request = save.Request;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var extraction = (await db.StatementExtractions
                .FromSql($"SELECT * FROM statement_extractions WHERE id = {save.ExtractionId} FOR UPDATE")
                .ToListAsync(ct))
            .SingleOrDefault();

        var holderId = request.IdempotencyKey is null
            ? null
            : await db.SavedRequests
                .Where(r => r.Source == request.Source && r.IdempotencyKey == request.IdempotencyKey)
                .Select(r => (long?)r.Id)
                .SingleOrDefaultAsync(ct);
        var holder = holderId is null ? null : await LoadAsync(db, holderId.Value, ct);
        if (holder is not null && holder.OriginalSha256 == Sha256(request.OriginalBody))
        {
            return Answer(UploadSaveKind.Replayed, holder);
        }

        if (extraction is null || extraction.ExpiresUtc <= request.NowUtc)
        {
            if (extraction is { SavedRequestId: null })
            {
                db.StatementExtractions.Remove(extraction);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            return Answer(UploadSaveKind.Expired, null);
        }

        if (extraction.UserId != save.UserId)
        {
            return Answer(UploadSaveKind.NotFound, null);
        }

        if (extraction.SavedRequestId is { } savedId)
        {
            var publicRef = await db.SavedRequests.Where(r => r.Id == savedId).Select(r => r.PublicRef).SingleAsync(ct);
            return new UploadSaveResult(UploadSaveKind.AlreadySaved, null, savedId, publicRef);
        }

        // SPEC-GAP T-1210 (Q-111): same key, other body, extraction not saved; KeyReused as CreateAsync, nothing written.
        if (holder is not null)
        {
            return Answer(UploadSaveKind.KeyReused, holder);
        }

        // The upload columns come from the locked record, never from the request body (spec 7.1).
        var entity = NewRequestEntity(request, save.Version2Payload is null ? 1 : 2);
        entity.UploadExtractionId = extraction.Id;
        entity.UploadFileName = extraction.FileName;
        entity.UploadSizeBytes = extraction.SizeBytes;
        entity.UploadSha256 = extraction.Sha256;
        entity.UploadModel = extraction.Model;
        entity.UploadPromptVersion = extraction.PromptVersion;
        entity.UploadExtractedUtc = extraction.ExtractedUtc;

        // Version 1 is the current version only when there is no version 2; otherwise it is read on its own.
        var read1 = save.Version2Payload is null ? request.Read : SavedPayloadReader.Read(save.Version1Payload);
        entity.Versions.Add(NewVersionEntity(1, save.Version1Payload, read1, "[]", save.Version1Note, request));
        if (save.Version2Payload is { } version2)
        {
            var changes = save.Version2Changes?.ToJsonString() ?? "[]";
            entity.Versions.Add(NewVersionEntity(2, version2, request.Read, changes, save.Version2Note, request));
        }

        entity.Events.Add(Event(
            SavedRequestEvents.Created, new JsonObject { ["source"] = "upload", ["fileName"] = extraction.FileName },
            request.NowUtc, request.CreatedBy));
        db.SavedRequests.Add(entity);
        await db.SaveChangesAsync(ct);
        await FlagDuplicateAsync(db, entity.Id, request, ct);

        extraction.SavedRequestId = entity.Id;
        await db.SaveChangesAsync(ct);
        var record = await LoadAsync(db, entity.Id, ct);
        await tx.CommitAsync(ct);
        return Answer(UploadSaveKind.Created, record);
    }

    public async Task<IReadOnlyList<UploadDuplicateCandidate>> UploadDuplicateCandidatesAsync(
        string sha256, DateTime sinceUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await (
                from r in db.SavedRequests.AsNoTracking()
                where r.CreatedUtc >= sinceUtc || r.UploadSha256 == sha256
                join v in db.SavedRequestVersions
                    on new { Id = r.Id, Number = r.CurrentVersion } equals new { Id = v.SavedRequestId, v.Number }
                orderby r.CreatedUtc descending, r.Id descending
                select new
                {
                    r.Id, r.Reference, r.Status, ReceivedUtc = r.EmailReceivedUtc ?? r.CreatedUtc, r.CreatedUtc,
                    HasEmail = r.EmailSubject != null || r.EmailReceivedUtc != null || r.EmailFrom != null || r.EmailMessageId != null,
                    HasUpload = r.UploadExtractionId != null,
                    r.UploadSha256, r.RowFingerprints, v.Payload,
                })
            .ToListAsync(ct);
        return rows
            .Select(r => new UploadDuplicateCandidate(
                r.Id, r.Reference, r.Status, r.ReceivedUtc, r.CreatedUtc,
                r.HasEmail ? "email" : r.HasUpload ? "upload" : "other",
                r.UploadSha256,
                new HashSet<string>(r.RowFingerprints, StringComparer.Ordinal),
                UploadDuplicateMatcher.RowKeysOf(ParseElement(r.Payload))))
            .ToList();
    }

    private static UploadSaveResult Answer(UploadSaveKind kind, SavedRequest? record) => new(kind, record, null, null);

    /// <summary>The upload columns as <see cref="UploadMeta"/>; null when the request was not saved from an upload.</summary>
    private static UploadMeta? UploadOf(
        Guid? extractionId, string? fileName, long? sizeBytes, string? sha256, string? model, string? promptVersion,
        DateTime? extractedUtc) =>
        extractionId is null
            ? null
            : new UploadMeta(
                fileName!, sizeBytes!.Value, sha256!, model!, promptVersion!,
                DateTime.SpecifyKind(extractedUtc!.Value, DateTimeKind.Utc));
}
