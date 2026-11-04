using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Pipeline;

/// <summary>FR-15 answer: list sizes and the <c>rules.json</c> accounts QuickBooks does not have.</summary>
public sealed record ListSyncResult(int Accounts, int Vendors, int Customers, IReadOnlyList<string> MissingInRules);

/// <summary>M14 §4.5: one successful sync: its answer, the run it recorded, and the lists it stored.</summary>
public sealed record ListSyncCompletion(ListSyncResult Result, ListSyncRun Run, QbLists Lists);

/// <summary>
/// FR-15, M14 §4: queries the active accounts, vendors and customers, hands them to <see cref="IQbListsStore"/>, and
/// reports the account names in <c>rules.json</c> that QuickBooks does not have. Every attempt is recorded; the current
/// lists change only when the rules and the query both succeed. A failure is recorded and then rethrown unchanged.
/// <para>
/// M17 §4.7: <paramref name="store"/> and <paramref name="source"/> are the default company's; a sync of a
/// <see cref="CompanyContext"/> records into and reads <paramref name="catalog"/>'s lists of that company only.
/// </para>
/// </summary>
public sealed class QbListSync(
    PipelineOptions options, IQbGateway gateway, IClock clock, IQbListsStore store, IQbListsSource source, IQbListsCatalog? catalog = null)
{
    /// <summary>SPEC-GAP T-602: sync has no job folder, so its audit copies live next to <c>qb-lists.json</c>.</summary>
    public const string AuditDir = "qb-audit";

    public string AuditFolder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.QbListsFile))!, AuditDir);

    public async Task<ListSyncResult> SyncAsync(ListSyncTrigger trigger, string? requestedBy, CancellationToken ct) =>
        (await SyncWithRunAsync(trigger, requestedBy, ct)).Result;

    /// <summary>As <see cref="SyncAsync"/>, and also returns the run it recorded and the lists it stored (§4.5).</summary>
    public Task<ListSyncCompletion> SyncWithRunAsync(ListSyncTrigger trigger, string? requestedBy, CancellationToken ct) =>
        SyncWithRunAsync(options.RulesFile, gateway, AuditFolder, trigger, requestedBy, ct);

    /// <summary>
    /// M17 §4.7: the sync of one company: its rules decide <c>MissingInRules</c>, its file is the one queried, its own
    /// lists and history are the ones recorded (never another company's, A-8), and a non-default company keeps its audit
    /// copies beside its ledger. A company that may not open a session is refused with
    /// <see cref="CompanyNotReadyException"/> before anything is sent or recorded. QuickBooks having another file open, or
    /// the file not opening, is recorded as <c>skipped</c> (<see cref="ListSyncReasons.CompanyFileOpenElsewhere"/>,
    /// <see cref="ListSyncReasons.CompanyFileUnavailable"/>) and rethrown; it is never retried here.
    /// </summary>
    public Task<ListSyncCompletion> SyncWithRunAsync(
        CompanyContext company, ListSyncTrigger trigger, string? requestedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (!company.CanOpenSession)
        {
            return Task.FromException<ListSyncCompletion>(new CompanyNotReadyException(company));
        }

        var (companyStore, companySource) = ListsOf(company);
        return SyncWithRunAsync(
            company.RulesFile,
            gateway.For(company.Target),
            company.IsDefault ? AuditFolder : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(company.LedgerFile))!, AuditDir),
            companyStore,
            companySource,
            trigger,
            requestedBy,
            ct);
    }

    private Task<ListSyncCompletion> SyncWithRunAsync(
        string rulesFile, IQbGateway target, string auditFolder, ListSyncTrigger trigger, string? requestedBy, CancellationToken ct) =>
        SyncWithRunAsync(rulesFile, target, auditFolder, store, source, trigger, requestedBy, ct);

    /// <summary>The company's store and source; without a catalog only the default company has lists.</summary>
    private (IQbListsStore Store, IQbListsSource Source) ListsOf(CompanyContext company)
    {
        if (catalog is not null)
        {
            return (catalog.StoreFor(company.Id), catalog.SourceFor(company.Id));
        }

        return company.IsDefault
            ? (store, source)
            : throw new InvalidOperationException($"no lists catalog: company {company.Id} has no lists store");
    }

    private async Task<ListSyncCompletion> SyncWithRunAsync(
        string rulesFile,
        IQbGateway target,
        string auditFolder,
        IQbListsStore runs,
        IQbListsSource current,
        ListSyncTrigger trigger,
        string? requestedBy,
        CancellationToken ct)
    {
        var started = clock.UtcNow;
        string? reason = null;
        try
        {
            Rules rules;
            try
            {
                rules = Rules.Load(rulesFile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reason = ListSyncReasons.RulesUnreadable;
                throw;
            }

            var request = QbListQuery.Build(options.QbXmlVersion);
            AtomicFile.WriteAllText(Path.Combine(auditFolder, "sync-lists.request.qbxml"), request);

            var response = await target.ProcessAsync(request, ct);
            AtomicFile.WriteAllText(Path.Combine(auditFolder, "sync-lists.response.qbxml"), response);

            var lists = QbListQuery.Parse(response, clock.UtcNow);
            if (IsEmpty(lists) && HasCurrentNames(current))
            {
                // M14 §4: an unattended sync must not wipe working lists. A company that never had names still syncs (M15 A-8).
                throw new QbStatusException("QuickBooks answered with no accounts, vendors or customers");
            }

            // SPEC-GAP T-602: names compare exactly (ordinal); a case difference is reported so the rule can be corrected.
            var known = lists.Accounts.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var missing = rules.AccountNames().Where(n => !known.Contains(n)).ToList();
            var result = new ListSyncResult(lists.Accounts.Count, lists.Vendors.Count, lists.Customers.Count, missing);

            var run = new ListSyncRun
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
            };
            await runs.RecordAsync(run, lists, ct);
            return new ListSyncCompletion(result, run, lists);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // M17 §4.7: another company file open, or a file that would not open, is not a failure of this company's
            // sync; the attempt was not made.
            var notMade = ex switch
            {
                QuickBooksWrongCompanyException => ListSyncReasons.CompanyFileOpenElsewhere,
                QuickBooksCompanyFileUnavailableException => ListSyncReasons.CompanyFileUnavailable,
                _ => null,
            };
            try
            {
                await runs.RecordAsync(
                    new ListSyncRun
                    {
                        Trigger = trigger,
                        RequestedBy = requestedBy,
                        StartedUtc = started,
                        FinishedUtc = clock.UtcNow,
                        Outcome = notMade is not null ? ListSyncOutcome.Skipped : ListSyncOutcome.Failed,
                        Reason = notMade ?? reason ?? ReasonOf(ex),
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
    public Task RecordSkippedAsync(ListSyncTrigger trigger, string? requestedBy, string reason, CancellationToken ct) =>
        RecordSkippedAsync(store, trigger, requestedBy, reason, ct);

    /// <summary>M17 §4.7: records, in <paramref name="company"/>'s history, an attempt that was not made.</summary>
    public Task RecordSkippedAsync(CompanyContext company, ListSyncTrigger trigger, string? requestedBy, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(company);
        return RecordSkippedAsync(ListsOf(company).Store, trigger, requestedBy, reason, ct);
    }

    private Task RecordSkippedAsync(IQbListsStore runs, ListSyncTrigger trigger, string? requestedBy, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return runs.RecordAsync(
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

    private static bool IsEmpty(QbLists lists) =>
        lists.Accounts.Count == 0 && lists.Vendors.Count == 0 && lists.Customers.Count == 0;

    /// <summary>False for a file that cannot be read: it has no names to protect, and a sync may always replace it.</summary>
    private static bool HasCurrentNames(IQbListsSource current)
    {
        try
        {
            return !IsEmpty(current.Current);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// The reason code of a QuickBooks failure: the same families the API maps to 503 and 502
    /// (<c>QuickBooksEndpoints.QuickBooksProblem</c>); null for anything else. v2 answers with it as the problem type.
    /// </summary>
    public static string? ReasonOf(Exception ex) => ex switch
    {
        QuickBooksBusyException => ListSyncReasons.QuickBooksBusy,
        QuickBooksUnavailableException or QuickBooksCallException => ListSyncReasons.QuickBooksUnavailable,
        QbStatusException or FormatException or System.Xml.XmlException => ListSyncReasons.QbStatus,
        _ => null,
    };
}
