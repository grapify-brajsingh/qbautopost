using QbAutopost.Core.Abstractions;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Pipeline;

public sealed record UndoFailure(string TxnId, string Message);

/// <summary>FR-13 answer. <see cref="Found"/> false → the batch is not in the ledger.</summary>
public sealed record UndoResult
{
    public bool Found { get; init; }
    public string? JobId { get; init; }
    public int Deleted { get; init; }
    public IReadOnlyList<UndoFailure> Failed { get; init; } = [];

    /// <summary>Every ledger entry of the batch is now undone (and the batch has at least one entry).</summary>
    public bool BatchUndone { get; init; }

    public static UndoResult NotFound { get; } = new();
}

/// <summary>
/// FR-13: deletes every not-yet-undone ledger entry of a batch with one <c>TxnDelRq</c> message set, marks each entry
/// QuickBooks confirmed as <c>undone</c>, and marks the batch undone when none is left. The ledger is saved atomically.
/// A gateway failure propagates and marks nothing.
/// </summary>
public sealed class BatchUndo(PipelineOptions options, IQbGateway gateway)
{
    public async Task<UndoResult> UndoAsync(string batchId, string auditDir, CancellationToken ct)
    {
        var store = new LedgerStore(options.LedgerFile);
        var ledger = store.Load();
        var batch = ledger.Jobs.FirstOrDefault(j => SameBatch(j.BatchId, batchId));
        if (batch is null)
        {
            return UndoResult.NotFound;
        }

        var live = ledger.Posted.Where(p => SameBatch(p.BatchId, batch.BatchId) && !p.Undone).ToList();
        if (live.Count == 0)
        {
            // SPEC-GAP T-606: a batch that recorded no posted line (Q-18) is left as it is; QuickBooks is not called.
            return new UndoResult { Found = true, JobId = batch.JobId, BatchUndone = batch.Undone };
        }

        var txns = live.Select(p => new TxnToDelete(p.Kind, p.TxnId)).ToList();
        var request = QbTxnDelete.Build(txns, options.QbXmlVersion);
        var auditName = "undo-" + string.Concat(batch.BatchId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));
        AtomicFile.WriteAllText(Path.Combine(auditDir, auditName + ".request.qbxml"), request);

        var response = await gateway.ProcessAsync(request, ct);
        AtomicFile.WriteAllText(Path.Combine(auditDir, auditName + ".response.qbxml"), response);
        var results = QbTxnDelete.Parse(txns, response);

        var deleted = results.Where(r => r.Deleted).Select(r => r.Txn.TxnId).ToHashSet(StringComparer.Ordinal);
        var posted = ledger.Posted
            .Select(p => SameBatch(p.BatchId, batch.BatchId) && deleted.Contains(p.TxnId) ? p with { Undone = true } : p)
            .ToList();
        var batchUndone = posted.Where(p => SameBatch(p.BatchId, batch.BatchId)).All(p => p.Undone);
        var jobs = ledger.Jobs.Select(j => ReferenceEquals(j, batch) ? j with { Undone = batchUndone } : j).ToList();
        store.Save(ledger with { Jobs = jobs, Posted = posted });

        return new UndoResult
        {
            Found = true,
            JobId = batch.JobId,
            Deleted = deleted.Count,
            Failed = results.Where(r => !r.Deleted).Select(r => new UndoFailure(r.Txn.TxnId, r.Status.ToString())).ToList(),
            BatchUndone = batchUndone,
        };
    }

    // Batch ids are "<jobId>#<attempt>" and job ids are folder names, so they compare ignoring case (as Ledger.HasJob).
    private static bool SameBatch(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
