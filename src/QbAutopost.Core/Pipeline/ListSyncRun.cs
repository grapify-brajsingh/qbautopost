namespace QbAutopost.Core.Pipeline;

/// <summary>M14 §3: what started a list sync.</summary>
public enum ListSyncTrigger { Schedule, Dashboard, Api, Import }

/// <summary>M14 §3: how a list sync attempt ended.</summary>
public enum ListSyncOutcome { Ok, Failed, Skipped }

/// <summary>M14 §3/§4: the reason codes of a failed or skipped attempt.</summary>
public static class ListSyncReasons
{
    public const string QuickBooksNotOpen = "quickbooks-not-open";
    public const string WorkerBusy = "worker-busy";
    public const string QuickBooksUnavailable = "quickbooks-unavailable";
    public const string QuickBooksBusy = "quickbooks-busy";
    public const string QbStatus = "qb-status";
    public const string RulesUnreadable = "rules-unreadable";
}

/// <summary>
/// One list sync attempt (M14 §3). Counts and <see cref="MissingInRules"/> are set for <see cref="ListSyncOutcome.Ok"/> only;
/// <see cref="Error"/> is operator text (an exception message, never a payload) and is cut to <see cref="MaxErrorLength"/>.
/// </summary>
public sealed record ListSyncRun
{
    public const int MaxErrorLength = 1000;

    private readonly string? _error;

    public required ListSyncTrigger Trigger { get; init; }

    public string? RequestedBy { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime FinishedUtc { get; init; }

    public required ListSyncOutcome Outcome { get; init; }

    public string? Reason { get; init; }

    public string? Error
    {
        get => _error;
        init => _error = value is { Length: > MaxErrorLength } ? value[..MaxErrorLength] : value;
    }

    public int? Accounts { get; init; }

    public int? Vendors { get; init; }

    public int? Customers { get; init; }

    public IReadOnlyList<string>? MissingInRules { get; init; }
}
