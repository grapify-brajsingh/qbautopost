using System.Collections.Frozen;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// R31 (spec 7.2, Q-93): the held-row reasons that prove the row's transaction was never created in QuickBooks. A
/// failed post returns its saved request to <c>pending</c> only when <b>every</b> held row is one of these; any other
/// reason (no-response, busy, amount-mismatch, a status-0 rejection, or a code added later and not reviewed here)
/// locks it as <c>unknown</c>, because a re-post of something that may have landed is a double post.
/// </summary>
public static class NothingSentReasons
{
    /// <summary>
    /// Reasons written before the add for that row was sent. Each comes from a gate that holds the row back, so its
    /// qbXML never left the process.
    /// <list type="bullet">
    /// <item><c>unknown-account</c>, <c>unknown-payee</c> (DirectPlanner): a name not in qb-lists.json.</item>
    /// <item><c>conflicting-last4</c> (DirectPlanner): one last four sent as two accounts.</item>
    /// <item><c>duplicate-line</c> (DirectPlanner): the same row twice in one request.</item>
    /// <item><c>no-account-rule</c>, <c>no-holding-account</c>, <c>no-deposit-income-account</c> (DirectPlanner): no
    /// account to post to.</item>
    /// <item><c>refnumber-too-long</c> (Mapper): the qbXML would be refused, so it is not built.</item>
    /// <item><c>possible-duplicate</c> (G4, LiveDuplicateGate): QuickBooks may already hold it.</item>
    /// <item><c>duplicate-check-failed</c> (G4, DirectPostRunner): the duplicate query failed; only queries were sent.</item>
    /// <item><c>backup-too-old</c> (FR-11, DirectPostRunner): refused before anything was sent.</item>
    /// <item><c>quickbooks-unavailable</c> (DirectPostRunner): the session could not be opened, so nothing reached
    /// QuickBooks.</item>
    /// </list>
    /// </summary>
    public static readonly FrozenSet<string> PreSend = new[]
    {
        HoldReasons.UnknownAccount,
        HoldReasons.UnknownPayee,
        HoldReasons.ConflictingLast4,
        HoldReasons.DuplicateLine,
        HoldReasons.NoAccountRule,
        HoldReasons.NoHoldingAccount,
        HoldReasons.NoDepositIncomeAccount,
        HoldReasons.RefNumberTooLong,
        HoldReasons.PossibleDuplicate,
        HoldReasons.DuplicateCheckFailed,
        HoldReasons.BackupTooOld,
        HoldReasons.QuickBooksUnavailable,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="row"/> proves nothing was created for it: a <see cref="PreSend"/> reason, or
    /// <c>quickbooks-rejected</c> with a positive status code and no TxnID (<see cref="PostVerifier.IsCleanRejection"/>):
    /// QuickBooks refused the add outright. A rejection with status 0 (accepted, but no TxnID came back), a missing
    /// status code, or a TxnID returned beside a warning is not proof.
    /// </summary>
    public static bool Proves(PostHeldRow row) =>
        row.Reason is { } reason
        && (PreSend.Contains(reason)
            || (reason == HoldReasons.QuickBooksRejected && PostVerifier.IsCleanRejection(row.Note)));
}
