using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Pipeline;

/// <summary>FR-15 answer: list sizes and the <c>rules.json</c> accounts QuickBooks does not have.</summary>
public sealed record ListSyncResult(int Accounts, int Vendors, int Customers, IReadOnlyList<string> MissingInRules);

/// <summary>
/// FR-15: queries the active accounts, vendors and customers, writes <c>qb-lists.json</c> atomically, and reports the
/// account names in <c>rules.json</c> that QuickBooks does not have. Nothing is written when the rules or the query fail.
/// </summary>
public sealed class QbListSync(PipelineOptions options, IQbGateway gateway, IClock clock)
{
    /// <summary>SPEC-GAP T-602: sync has no job folder, so its audit copies live next to <c>qb-lists.json</c>.</summary>
    public const string AuditDir = "qb-audit";

    public string AuditFolder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.QbListsFile))!, AuditDir);

    public async Task<ListSyncResult> SyncAsync(CancellationToken ct)
    {
        var rules = Rules.Load(options.RulesFile);
        var request = QbListQuery.Build(options.QbXmlVersion);
        AtomicFile.WriteAllText(Path.Combine(AuditFolder, "sync-lists.request.qbxml"), request);

        var response = await gateway.ProcessAsync(request, ct);
        AtomicFile.WriteAllText(Path.Combine(AuditFolder, "sync-lists.response.qbxml"), response);

        var lists = QbListQuery.Parse(response, clock.UtcNow);
        new QbListsStore(options.QbListsFile).Save(lists);

        // SPEC-GAP T-602: names compare exactly (ordinal); a case difference is reported so the rule can be corrected.
        var known = lists.Accounts.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var missing = rules.AccountNames().Where(n => !known.Contains(n)).ToList();
        return new ListSyncResult(lists.Accounts.Count, lists.Vendors.Count, lists.Customers.Count, missing);
    }
}
