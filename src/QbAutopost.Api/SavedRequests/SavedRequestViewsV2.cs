using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.SavedRequests;

/// <summary>Dashboard v2 spec 5.1: the email a request came from, as read at its v2 save. Every field may be null.</summary>
public sealed record EmailView(string? Subject, DateTime? ReceivedUtc, string? From, string? MessageId);

/// <summary>
/// Dashboard v2 spec 5: the answer to a v2 save. The v1 fields, with <c>needsAttention</c> and <c>validation</c> taken
/// from the v2 judgement (spec 5.3, 6.4), plus <c>pushId</c> and <c>email</c>.
/// </summary>
public sealed record SavedV2View(
    long Id,
    string PublicRef,
    SavedRequestStatus Status,
    bool NeedsAttention,
    DuplicateView Duplicate,
    ValidationView? Validation,
    DateTime CreatedUtc,
    string? PushId,
    EmailView? Email);

/// <summary>The v2 views of a saved request. v1 views (<see cref="SavedRequestViews"/>) never show these fields.</summary>
public static class SavedRequestViewsV2
{
    public static SavedV2View Saved(SavedRequest r) =>
        new(r.Id, r.PublicRef, r.Status, r.NeedsAttentionV2,
            SavedRequestViews.Duplicate(r.DuplicateOfId, r.DuplicateReasons),
            SavedRequestViews.Validation(r.ValidationV2), r.CreatedUtc, PushId(r.Metadata), Email(r.Metadata));

    /// <summary>Spec 5.2: lower case with dashes, or null.</summary>
    public static string? PushId(SaveMetadata m) => m.PushId?.ToString("D");

    /// <summary>Spec 5.1: null when all four columns are null.</summary>
    public static EmailView? Email(SaveMetadata m) =>
        m.HasEmail ? new EmailView(m.EmailSubject, m.EmailReceivedUtc, m.EmailFrom, m.EmailMessageId) : null;
}
