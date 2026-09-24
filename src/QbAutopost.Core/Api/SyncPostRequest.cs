namespace QbAutopost.Core.Api;

/// <summary>
/// The body of <c>POST /api/v1/quickbooks/transactions/validate-and-post</c> (validate-and-post §4): api-v1 §6.1's
/// request <b>minus <c>dryRun</c></b>. The row shape and every field rule are unchanged, so a caller can move
/// between this route and <see cref="DirectRequest"/>'s without rewriting a single row.
/// <para>
/// <see cref="DryRun"/> and <see cref="AllowModelAccounts"/> are declared here for one reason: so that a caller who
/// sends either is <b>refused</b> (400) rather than served. A field the binder quietly drops would be accepted in
/// silence, and a caller who asked for a dry run and was answered "posted" would have moved money they believed
/// they were only checking. That is the shape of the T-915 defect; these two fields exist to be read by the refusal
/// in <c>SyncPostRunner</c>, and by nothing else.
/// </para>
/// </summary>
public sealed record SyncPostRequest
{
    /// <summary>Optional; <c>^[A-Za-z0-9._-]{1,64}$</c>. Names the ledger row <c>api-&lt;reference&gt;</c>.</summary>
    public string? Reference { get; init; }

    /// <summary>Required (§4). The caller's own arithmetic: checked to the cent, never corrected (CLAUDE.md rule 3).</summary>
    public decimal? ControlTotal { get; init; }

    public IReadOnlyList<DirectRow> Transactions { get; init; } = [];

    /// <summary>
    /// Not a field of this route (§2.3). Any value at all — including <c>false</c> — is refused, because a caller
    /// who sends it is on the wrong route and should be told so rather than guessed at.
    /// </summary>
    public bool? DryRun { get; init; }

    /// <summary>Refused as on the direct path (T-915): tiers 3–4 never run here, so the flag would do nothing.</summary>
    public bool? AllowModelAccounts { get; init; }

    /// <summary>
    /// The same request in the shape the planner and the post runner already take, so neither is reimplemented for
    /// this route. Both refused fields are pinned off rather than carried across — a request that set either one
    /// never reaches here.
    /// </summary>
    public DirectRequest ToDirectRequest() => new()
    {
        Reference = Reference,
        ControlTotal = ControlTotal,
        Transactions = Transactions,
        DryRun = false,
        AllowModelAccounts = false,
    };
}
