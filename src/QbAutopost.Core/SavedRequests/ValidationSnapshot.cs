using QbAutopost.Core.Api;

namespace QbAutopost.Core.SavedRequests;

public enum ValidationState { Ok, Held, Refused, Unreadable, Unavailable }

/// <summary>What the last validation of one saved version said (spec §6.4).</summary>
public sealed record ValidationSnapshot(
    ValidationState State,
    DateTime ValidatedUtc,
    int VersionNumber,
    SyncPostFindingCounts? Counts,
    IReadOnlyList<SyncPostFinding> Validation,
    IReadOnlyList<string> Errors,
    DirectUnknownNames? UnknownNames)
{
    public bool NeedsAttention => State is ValidationState.Held or ValidationState.Refused or ValidationState.Unreadable;
}
