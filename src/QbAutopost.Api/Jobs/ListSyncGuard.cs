using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Jobs;

/// <summary>M14 §6, shared by the scheduled sync and Sync now: the presence check, and the row of an attempt it stops.</summary>
public static class ListSyncGuard
{
    /// <summary>§6: never start QuickBooks. A check that itself fails counts as "not open" — hold, don't guess.</summary>
    // SPEC-GAP T-1406 (Q-122): a throwing check is "not open".
    public static bool IsOpen(IQbPresence presence, ILogger log)
    {
        try
        {
            return presence.IsCompanyOpen();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "QuickBooks presence check failed; the list sync is treated as not open");
            return false;
        }
    }

    /// <summary>Records the skipped attempt. A failure to record it changes nothing for the caller: the store has logged it.</summary>
    public static Task RecordSkippedAsync(
        QbListSync sync, ListSyncTrigger trigger, string? requestedBy, ILogger log, CancellationToken ct) =>
        RecordSkippedAsync(sync, null, trigger, requestedBy, log, ct);

    /// <summary>M17 §4.7: as above, in <paramref name="company"/>'s history; null is the default company as before M17.</summary>
    public static async Task RecordSkippedAsync(
        QbListSync sync, CompanyContext? company, ListSyncTrigger trigger, string? requestedBy, ILogger log, CancellationToken ct)
    {
        try
        {
            await (company is null
                ? sync.RecordSkippedAsync(trigger, requestedBy, ListSyncReasons.QuickBooksNotOpen, ct)
                : sync.RecordSkippedAsync(company, trigger, requestedBy, ListSyncReasons.QuickBooksNotOpen, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogInformation("QuickBooks lists sync: the skipped attempt could not be recorded: {Error}", ex.Message);
        }
    }
}
