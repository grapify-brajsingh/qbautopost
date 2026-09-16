using QbAutopost.Core.Models;

namespace QbAutopost.Core.Store;

/// <summary><c>ledger.json</c>: every posted line with its TxnID and fingerprint (spec §13).</summary>
public sealed record Ledger
{
    public IReadOnlyList<LedgerJob> Jobs { get; init; } = [];
    public IReadOnlyList<LedgerEntry> Posted { get; init; } = [];

    public static Ledger Empty { get; } = new();

    /// <summary>Spec F3: a job id already in the ledger is refused unless forced. Job ids are folder names, so case-insensitive.</summary>
    public bool HasJob(string jobId) =>
        Jobs.Any(j => string.Equals(j.JobId, jobId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Spec G4: a live (not undone) entry with this fingerprint means the line is already posted.</summary>
    public bool IsPosted(string fingerprint) =>
        Posted.Any(p => !p.Undone && string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal));
}

public sealed record LedgerJob
{
    public required string JobId { get; init; }
    public required string BatchId { get; init; }
    public DateTime PostedUtc { get; init; }
    public int Posted { get; init; }
    public int Held { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> TxnIds { get; init; } = [];
    public bool Undone { get; init; }
}

public sealed record LedgerEntry
{
    public required string BatchId { get; init; }
    public required string JobId { get; init; }
    public required string Fingerprint { get; init; }
    public required string TxnId { get; init; }
    public string? EditSequence { get; init; }
    public required TxnKind Kind { get; init; }
    public required string Account { get; init; }
    public string? Payee { get; init; }
    public string? LineAccount { get; init; }
    public required decimal Amount { get; init; }
    public required DateOnly Date { get; init; }
    public string? RefNumber { get; init; }
    public string Memo { get; init; } = "";
    public required string SourceFile { get; init; }
    public int LineNo { get; init; }
    public bool Undone { get; init; }
}
