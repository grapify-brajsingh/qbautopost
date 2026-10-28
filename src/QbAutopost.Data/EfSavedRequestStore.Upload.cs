using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using QbAutopost.Core.Extraction;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>The extraction records (T-1209, dashboard-api-m12 6.6).</summary>
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
}
