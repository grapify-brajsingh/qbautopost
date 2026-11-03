using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QbAutopost.Core.Companies;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Core.Text;
using QbAutopost.Data.Configurations;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>
/// <see cref="ICompanyStore"/> on PostgreSQL (M17 spec 4.3, 4.4). A singleton: every call makes its own context. Folder
/// identity is case-insensitive (ruling B10), through the database's <c>folder_key</c> column. Each mutation writes its
/// <c>user_audit</c> row in the same <c>SaveChanges</c> (plan conflict 4).
/// </summary>
public sealed class EfCompanyStore(IDbContextFactory<QbAutopostDbContext> factory) : ICompanyStore
{
    private const string DefaultAccessMessage = "the configured Company:FilePath, used before M17";

    public async Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Companies.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<Company?> GetAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<DefaultCompanyResult> EnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            return await TryEnsureDefaultAsync(company, nowUtc, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A second process created the default between our check and our insert; its transaction won.
            return await ExistingDefaultAsync(ct);
        }
    }

    private async Task<DefaultCompanyResult> TryEnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (await db.Companies.AnyAsync(ct))
        {
            return await ExistingDefaultAsync(ct);
        }

        var row = new CompanyEntity
        {
            FolderName = company.FolderName,
            Code = company.Code,
            DisplayName = company.DisplayName,
            FilePath = company.FilePath,
            CandidateFiles = Serialize([company.FilePath]),
            State = CompanyState.Active,
            // Plan conflict 2: the upgraded single-company install must keep working without a Check access.
            Access = CompanyAccess.Approved,
            AccessCheckedUtc = nowUtc,
            AccessMessage = DefaultAccessMessage,
            MatchNames = "[]",
            IsDefault = true,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            RowVersion = 1,
        };
        db.Companies.Add(row);
        await db.SaveChangesAsync(ct);

        var id = row.Id;
        var requests = await db.SavedRequests.Where(r => r.CompanyId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.CompanyId, id)
                .SetProperty(r => r.CompanySource, CompanySource.Migrated), ct);
        var extractions = await db.StatementExtractions.Where(e => e.CompanyId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CompanyId, id), ct);
        var syncs = await db.QbListSyncs.Where(e => e.CompanyId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CompanyId, id), ct);

        await tx.CommitAsync(ct);
        return new DefaultCompanyResult(ToModel(row), true, requests + extractions + syncs);
    }

    private async Task<DefaultCompanyResult> ExistingDefaultAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.IsDefault, ct)
            ?? throw new InvalidOperationException("The companies table has rows but none is the default company.");
        return new DefaultCompanyResult(ToModel(row), false, 0);
    }

    public async Task<ScanApplied> ApplyScanAsync(ScanPlan plan, string actor, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var touched = new List<CompanyEntity>();
        var addedRows = new List<CompanyEntity>();
        var updated = 0;

        foreach (var change in plan.Add.Concat(plan.Update))
        {
            var existing = await FindAsync(db, change, ct);
            if (existing is null)
            {
                var row = NewRow(change, nowUtc);
                db.Companies.Add(row);
                addedRows.Add(row);
                touched.Add(row);
            }
            else
            {
                Apply(existing, change, nowUtc);
                updated++;
                touched.Add(existing);
            }
        }

        var missing = 0;
        foreach (var id in plan.Missing)
        {
            var row = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row is null || row.State is CompanyState.Missing or CompanyState.Disabled)
            {
                continue;
            }

            row.State = CompanyState.Missing;
            Touch(row, nowUtc);
            missing++;
            touched.Add(row);
        }

        var ambiguous = plan.Ambiguous;
        db.UserAudit.Add(Audit(AuditActions.CompaniesScanned, actor, nowUtc, new JsonObject
        {
            ["added"] = addedRows.Count,
            ["updated"] = updated,
            ["missing"] = missing,
            ["ambiguous"] = ambiguous,
            ["noCompanyFile"] = new JsonArray(plan.NoCompanyFile.Select(n => (JsonNode?)n).ToArray()),
        }));
        await db.SaveChangesAsync(ct);

        return new ScanApplied(
            addedRows.Count, updated, missing, ambiguous,
            addedRows.Select(r => r.Id).ToList(),
            touched.DistinctBy(r => r.Id).OrderBy(r => r.Id).Select(ToModel).ToList());
    }

    public async Task<CompanyChange> UpdateAsync(
        int id, int expectedRowVersion, CompanyPatch patch, string actor, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null)
        {
            return new CompanyChange(CompanyChangeKind.NotFound, null);
        }

        if (row.RowVersion != expectedRowVersion)
        {
            return new CompanyChange(CompanyChangeKind.Stale, ToModel(row));
        }

        var detail = new JsonObject { ["companyId"] = id };
        var changed = false;

        if (patch.FilePath is { } path)
        {
            var candidates = Deserialize(row.CandidateFiles);
            if (!candidates.Any(c => SamePath(c, path)) && !SamePath(row.FilePath, path))
            {
                return new CompanyChange(CompanyChangeKind.Invalid, ToModel(row), "filePath is not one of the company's candidate files");
            }

            if (!SamePath(row.FilePath, path))
            {
                // The file changed, so an earlier access check says nothing about it.
                row.FilePath = candidates.FirstOrDefault(c => SamePath(c, path)) ?? path;
                row.Access = CompanyAccess.Unchecked;
                row.AccessCheckedUtc = null;
                row.AccessMessage = null;
                detail["filePath"] = row.FilePath;
                changed = true;
            }

            if (row.State == CompanyState.Ambiguous)
            {
                row.State = CompanyState.Active;
                detail["state"] = CompanyText.Of(row.State);
                changed = true;
            }
        }

        if (patch.Disabled is { } disabled)
        {
            if (disabled && row.IsDefault)
            {
                return new CompanyChange(CompanyChangeKind.Invalid, ToModel(row), "the default company cannot be disabled");
            }

            if (disabled && row.State != CompanyState.Disabled)
            {
                row.State = CompanyState.Disabled;
                detail["state"] = CompanyText.Of(row.State);
                changed = true;
            }
            else if (!disabled && row.State == CompanyState.Disabled)
            {
                row.State = row.FilePath is null ? CompanyState.Ambiguous : CompanyState.Active;
                detail["state"] = CompanyText.Of(row.State);
                changed = true;
            }
        }

        if (patch.MatchNames is { } names)
        {
            var cleaned = names.Select(n => n.Trim()).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!cleaned.SequenceEqual(Deserialize(row.MatchNames), StringComparer.Ordinal))
            {
                row.MatchNames = Serialize(cleaned);
                detail["matchNames"] = new JsonArray(cleaned.Select(n => (JsonNode?)n).ToArray());
                changed = true;
            }
        }

        if (!changed)
        {
            return new CompanyChange(CompanyChangeKind.Changed, ToModel(row));
        }

        Touch(row, nowUtc);
        db.UserAudit.Add(Audit(AuditActions.CompanyUpdated, actor, nowUtc, detail));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else wrote between our read and our update.
            return new CompanyChange(CompanyChangeKind.Stale, null);
        }

        return new CompanyChange(CompanyChangeKind.Changed, ToModel(row));
    }

    public async Task<CompanyChange> RecordAccessAsync(int id, CompanyAccessCheck check, string actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null)
        {
            return new CompanyChange(CompanyChangeKind.NotFound, null);
        }

        row.Access = check.Access;
        row.AccessMessage = check.Message;
        row.AccessCheckedUtc = check.CheckedUtc;
        row.QbCompanyName = check.QbCompanyName ?? row.QbCompanyName;
        row.QbLegalName = check.QbLegalName ?? row.QbLegalName;
        Touch(row, check.CheckedUtc);
        db.UserAudit.Add(Audit(AuditActions.CompanyChecked, actor, check.CheckedUtc, new JsonObject
        {
            ["companyId"] = id,
            ["access"] = CompanyText.Of(check.Access),
        }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CompanyChange(CompanyChangeKind.Stale, null);
        }

        return new CompanyChange(CompanyChangeKind.Changed, ToModel(row));
    }

    public async Task<DateTime?> LastScanUtcAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var at = await db.UserAudit.Where(a => a.Kind == AuditActions.CompaniesScanned).MaxAsync(a => (DateTime?)a.AtUtc, ct);
        return at is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static async Task<CompanyEntity?> FindAsync(QbAutopostDbContext db, FolderChange change, CancellationToken ct)
    {
        if (change.CompanyId > 0 && await db.Companies.FirstOrDefaultAsync(c => c.Id == change.CompanyId, ct) is { } byId)
        {
            return byId;
        }

        // Folder identity is case-insensitive (ruling B10): the same lower() as the unique index.
        var name = change.Folder.FolderName.ToLowerInvariant();
        return await db.Companies.FirstOrDefaultAsync(c => c.FolderKey == name, ct);
    }

    private static CompanyEntity NewRow(FolderChange change, DateTime nowUtc) => new()
    {
        FolderName = change.Folder.FolderName,
        Code = change.Folder.Code,
        DisplayName = change.Folder.DisplayName,
        FilePath = change.FilePath,
        CandidateFiles = Serialize(change.Folder.Files),
        State = change.State,
        Access = CompanyAccess.Unchecked,
        MatchNames = "[]",
        IsDefault = false,
        CreatedUtc = nowUtc,
        UpdatedUtc = nowUtc,
        RowVersion = 1,
    };

    private static void Apply(CompanyEntity row, FolderChange change, DateTime nowUtc)
    {
        var fileChanged = !SamePath(row.FilePath, change.FilePath);
        row.FolderName = change.Folder.FolderName;
        row.Code = change.Folder.Code;
        if (!row.IsDefault)
        {
            // The default company keeps the name it was configured with.
            row.DisplayName = change.Folder.DisplayName;
        }

        row.FilePath = change.FilePath;
        row.CandidateFiles = Serialize(change.Folder.Files);
        row.State = change.State;
        if (fileChanged)
        {
            row.Access = CompanyAccess.Unchecked;
            row.AccessCheckedUtc = null;
            row.AccessMessage = null;
        }

        Touch(row, nowUtc);
    }

    private static void Touch(CompanyEntity row, DateTime nowUtc)
    {
        row.UpdatedUtc = nowUtc;
        row.RowVersion++;
    }

    private static UserAuditEntity Audit(string kind, string actor, DateTime nowUtc, JsonObject detail) => new()
    {
        UserId = null,
        Actor = actor,
        Kind = kind,
        Detail = detail.ToJsonString(),
        AtUtc = nowUtc,
    };

    private static bool SamePath(string? a, string? b) =>
        a is null || b is null ? a is null && b is null : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Serialize(IEnumerable<string> values) => JsonSerializer.Serialize(values, JsonOptions.Default);

    private static List<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions.Default) ?? [];

    private static Company ToModel(CompanyEntity r) => new(
        r.Id, r.FolderName, r.Code, r.DisplayName, r.FilePath, Deserialize(r.CandidateFiles), r.State, r.Access,
        Utc(r.AccessCheckedUtc), r.AccessMessage, r.QbCompanyName, r.QbLegalName, Deserialize(r.MatchNames), r.IsDefault,
        DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc), DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc), r.RowVersion);

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
