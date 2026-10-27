namespace QbAutopost.Core.SavedRequests;

/// <summary>Lifecycle of a saved request (spec saved-requests-postgres §7.1). Serialised by the API as kebab-case lower.</summary>
public enum SavedRequestStatus
{
    Pending,
    Posting,
    Posted,
    Partial,
    Unknown,
    Discarded,
}

/// <summary>How an admin resolves a <c>partial</c> or <c>unknown</c> request.</summary>
public enum ResolveOutcome
{
    Posted,
    NotPosted,
}

/// <summary>What a single post attempt ended as (spec §7.2). Serialised by the API as kebab-case lower.</summary>
public enum AttemptOutcome
{
    InProgress,
    Posted,
    Partial,
    Failed,
    Held,
    Refused,
    Busy,
    Unavailable,
    Error,
    Unknown,
}

/// <summary>Actions a caller may take on a saved request.</summary>
[Flags]
public enum SavedRequestAction
{
    None = 0,
    Edit = 1,
    Validate = 2,
    Post = 4,
    Discard = 8,
    Restore = 16,
    Resolve = 32,
}
