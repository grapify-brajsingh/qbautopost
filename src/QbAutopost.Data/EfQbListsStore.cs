using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Text;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>
/// <see cref="IQbListsStore"/> on PostgreSQL (M14 spec §3): the lists of the newest Ok attempt are the current lists.
/// A singleton: every call makes its own context from the pooled factory. <paramref name="historyRows"/> is
/// <c>QuickBooks:ListSync:HistoryRows</c>, validated by the caller.
/// </summary>
public sealed class EfQbListsStore(IDbContextFactory<QbAutopostDbContext> factory, int historyRows = EfQbListsStore.DefaultHistoryRows)
    : IQbListsStore
{
    public const int DefaultHistoryRows = 200;

    private static readonly JsonSerializerOptions Json = new(JsonOptions.Default) { WriteIndented = false };

    private readonly int _historyRows = historyRows > 0 ? historyRows : throw new ArgumentOutOfRangeException(nameof(historyRows));

    public async Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.QbListSyncs.Add(ToEntity(run, run.Outcome == ListSyncOutcome.Ok ? lists : null));
        await db.SaveChangesAsync(ct);
        await PruneAsync(db, ct);

        await transaction.CommitAsync(ct);
    }

    public async Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.QbListSyncs.AsNoTracking().OrderByDescending(r => r.Id)
            .Select(r => new
            {
                r.Trigger, r.RequestedBy, r.StartedUtc, r.FinishedUtc, r.Outcome, r.Reason, r.Error,
                r.Accounts, r.Vendors, r.Customers, r.MissingInRules,
            })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new ListSyncRun
            {
                Trigger = row.Trigger,
                RequestedBy = row.RequestedBy,
                StartedUtc = row.StartedUtc,
                FinishedUtc = row.FinishedUtc,
                Outcome = row.Outcome,
                Reason = row.Reason,
                Error = row.Error,
                Accounts = row.Accounts,
                Vendors = row.Vendors,
                Customers = row.Customers,
                MissingInRules = row.MissingInRules is null ? null : JsonSerializer.Deserialize<List<string>>(row.MissingInRules, Json),
            };
    }

    /// <summary>The <c>lists</c> of the newest Ok row; null when there is none.</summary>
    public async Task<QbLists?> LoadCurrentAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = await db.QbListSyncs.AsNoTracking()
            .Where(r => r.Outcome == ListSyncOutcome.Ok && r.Lists != null)
            .OrderByDescending(r => r.Id)
            .Select(r => r.Lists)
            .FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<QbLists>(json, Json);
    }

    /// <summary>
    /// M14 §3.3: when no Ok row exists, inserts <paramref name="fileLists"/> once as an Ok row with trigger
    /// <c>import</c>. Returns whether it inserted; a second call (or an existing Ok row) inserts nothing.
    /// </summary>
    public async Task<bool> ImportIfEmptyAsync(QbLists fileLists, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileLists);

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (await db.QbListSyncs.AnyAsync(r => r.Outcome == ListSyncOutcome.Ok, ct))
        {
            return false;
        }

        var run = new ListSyncRun
        {
            Trigger = ListSyncTrigger.Import,
            StartedUtc = nowUtc,
            FinishedUtc = nowUtc,
            Outcome = ListSyncOutcome.Ok,
            Accounts = fileLists.Accounts.Count,
            Vendors = fileLists.Vendors.Count,
            Customers = fileLists.Customers.Count,
        };
        db.QbListSyncs.Add(ToEntity(run, fileLists));
        await db.SaveChangesAsync(ct);
        await PruneAsync(db, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Deletes the rows beyond the newest <c>historyRows</c>, never the newest Ok row.</summary>
    private async Task PruneAsync(QbAutopostDbContext db, CancellationToken ct)
    {
        var cutoff = await db.QbListSyncs.OrderByDescending(r => r.Id).Skip(_historyRows).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (cutoff is null)
        {
            return;
        }

        var newestOk = await db.QbListSyncs.Where(r => r.Outcome == ListSyncOutcome.Ok).MaxAsync(r => (long?)r.Id, ct);
        await db.QbListSyncs.Where(r => r.Id <= cutoff && r.Id != newestOk).ExecuteDeleteAsync(ct);
    }

    private static QbListSyncEntity ToEntity(ListSyncRun run, QbLists? lists) => new()
    {
        Trigger = run.Trigger,
        RequestedBy = run.RequestedBy,
        StartedUtc = run.StartedUtc,
        FinishedUtc = run.FinishedUtc,
        Outcome = run.Outcome,
        Reason = run.Reason,
        Error = run.Error,
        Accounts = run.Accounts,
        Vendors = run.Vendors,
        Customers = run.Customers,
        MissingInRules = run.MissingInRules is null ? null : JsonSerializer.Serialize(run.MissingInRules, Json),
        Lists = lists is null ? null : JsonSerializer.Serialize(lists, Json),
    };
}
