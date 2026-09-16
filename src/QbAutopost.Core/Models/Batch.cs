namespace QbAutopost.Core.Models;

/// <summary>The mapped lines posted in one SDK message set (spec §3, §7).</summary>
public sealed record Batch
{
    public required string Id { get; init; }
    public required string JobId { get; init; }
    public required string Company { get; init; }
    public IReadOnlyList<MappedTxn> ToPost { get; init; } = [];
    public IReadOnlyList<MappedTxn> Held { get; init; } = [];
    public IReadOnlyList<MappedTxn> Skipped { get; init; } = [];
    public string QbXml { get; init; } = "";
    public IReadOnlyList<PostResult> Results { get; init; } = [];

    /// <summary>Batch id = job id + attempt, e.g. <c>2026-08-tropicana#1</c>.</summary>
    public static string MakeId(string jobId, int attempt) => $"{jobId}#{attempt}";
}

/// <summary>One <c>*AddRs</c> from a QuickBooks response (spec FR-12).</summary>
public sealed record PostResult
{
    public required string RequestId { get; init; }
    public required int StatusCode { get; init; }
    public string? StatusMessage { get; init; }
    public string? TxnId { get; init; }
    public string? EditSequence { get; init; }

    /// <summary>Echoed <c>Amount</c> or <c>DepositTotal</c>, when QuickBooks returned one.</summary>
    public decimal? Amount { get; init; }
}
