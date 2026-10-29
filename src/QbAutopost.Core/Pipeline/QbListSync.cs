using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Pipeline;

/// <summary>FR-15 answer: list sizes and the <c>rules.json</c> accounts QuickBooks does not have.</summary>
public sealed record ListSyncResult(int Accounts, int Vendors, int Customers, IReadOnlyList<string> MissingInRules);

/// <summary>
/// FR-15, M14 §4: queries the active accounts, vendors and customers, hands them to <see cref="IQbListsStore"/>, and
/// reports the account names in <c>rules.json</c> that QuickBooks does not have. Every attempt is recorded; the current
/// lists change only when the rules and the query both succeed. A failure is recorded and then rethrown unchanged.
/// </summary>
public sealed class QbListSync(PipelineOptions options, IQbGateway gateway, IClock clock, IQbListsStore store)
{
    /// <summary>SPEC-GAP T-602: sync has no job folder, so its audit copies live next to <c>qb-lists.json</c>.</summary>
    public const string AuditDir = "qb-audit";

    public string AuditFolder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.QbListsFile))!, AuditDir);

    public async Task<ListSyncResult> SyncAsync(ListSyncTrigger trigger, string? requestedBy, CancellationToken ct)
    {
        var started = clock.UtcNow;
        string? reason = null;
        try
        {
            Rules rules;
            try
            {
                rules = Rules.Load(options.RulesFile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reason = ListSyncReasons.RulesUnreadable;
                throw;
            }

            var request = QbListQuery.Build(options.QbXmlVersion);
            AtomicFile.WriteAllText(Path.Combine(AuditFolder, "sync-lists.request.qbxml"), request);

            var response = await gateway.ProcessAsync(request, ct);
            AtomicFile.WriteAllText(Path.Combine(AuditFolder, "sync-lists.response.qbxml"), response);

            var lists = QbListQuery.Parse(response, clock.UtcNow);

            // SPEC-GAP T-602: names compare exactly (ordinal); a case difference is reported so the rule can be corrected.
            var known = lists.Accounts.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var missing = rules.AccountNames().Where(n => !known.Contains(n)).ToList();
            var result = new ListSyncResult(lists.Accounts.Count, lists.Vendors.Count, lists.Customers.Count, missing);

            await store.RecordAsync(
                new ListSyncRun
                {
                    Trigger = trigger,
                    RequestedBy = requestedBy,
                    StartedUtc = started,
                    FinishedUtc = clock.UtcNow,
                    Outcome = ListSyncOutcome.Ok,
                    Accounts = result.Accounts,
                    Vendors = result.Vendors,
                    Customers = result.Customers,
                    MissingInRules = missing,
                },
                lists,
                ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                await store.RecordAsync(
                    new ListSyncRun
                    {
                        Trigger = trigger,
                        RequestedBy = requestedBy,
                        StartedUtc = started,
                        FinishedUtc = clock.UtcNow,
                        Outcome = ListSyncOutcome.Failed,
                        Reason = reason ?? ReasonOf(ex),
                        Error = ex.Message,
                    },
                    null,
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // The caller must see the sync failure, not a failure to write its record.
            }

            throw;
        }
    }

    /// <summary>Records an attempt that was not made (M14 §5, §6); nothing is sent to QuickBooks.</summary>
    public Task RecordSkippedAsync(ListSyncTrigger trigger, string? requestedBy, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return store.RecordAsync(
            new ListSyncRun
            {
                Trigger = trigger,
                RequestedBy = requestedBy,
                StartedUtc = now,
                FinishedUtc = now,
                Outcome = ListSyncOutcome.Skipped,
                Reason = reason,
            },
            null,
            ct);
    }

    /// <summary>The same families the API maps to 503 and 502 (<c>QuickBooksEndpoints.QuickBooksProblem</c>).</summary>
    private static string? ReasonOf(Exception ex) => ex switch
    {
        QuickBooksBusyException => ListSyncReasons.QuickBooksBusy,
        QuickBooksUnavailableException or QuickBooksCallException => ListSyncReasons.QuickBooksUnavailable,
        QbStatusException or FormatException or System.Xml.XmlException => ListSyncReasons.QbStatus,
        _ => null,
    };
}
