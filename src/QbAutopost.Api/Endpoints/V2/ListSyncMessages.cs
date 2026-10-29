using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>
/// M14 §7.1: the fixed, operator-safe sentence <c>lastSync.message</c> shows for an attempt. Chosen by outcome and
/// reason only; never the attempt's <see cref="ListSyncRun.Error"/>, which is an exception message.
/// </summary>
public static class ListSyncMessages
{
    public const string Kept = " The lists from the last good sync are still used.";

    public static string For(ListSyncRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return (run.Outcome, run.Reason) switch
        {
            (ListSyncOutcome.Ok, _) when run.Trigger == ListSyncTrigger.Import => "The lists were imported from qb-lists.json.",
            (ListSyncOutcome.Ok, _) => "The lists were synced from QuickBooks.",
            (ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen) => "QuickBooks was not open, so nothing was synced." + Kept,
            (ListSyncOutcome.Skipped, ListSyncReasons.WorkerBusy) => "The server was busy, so nothing was synced." + Kept,
            (ListSyncOutcome.Skipped, _) => "The sync was skipped." + Kept,
            (ListSyncOutcome.Failed, ListSyncReasons.QuickBooksUnavailable) => "QuickBooks could not be reached." + Kept,
            (ListSyncOutcome.Failed, ListSyncReasons.QuickBooksBusy) => "QuickBooks did not answer in time." + Kept,
            (ListSyncOutcome.Failed, ListSyncReasons.QbStatus) => "QuickBooks refused the list query." + Kept,
            (ListSyncOutcome.Failed, ListSyncReasons.RulesUnreadable) => "The rules file could not be read." + Kept,
            _ => "The sync failed." + Kept,
        };
    }
}
