using Microsoft.AspNetCore.Mvc;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.SavedRequests;

/// <summary>How a post-by-id request ended (saved-requests spec 7.2, 9, 10.2). Only <see cref="Ran"/> and <see cref="Unreadable"/> made an attempt.</summary>
public enum SavedPostKind
{
    /// <summary>Claimed and handed to the engine. The attempt is recorded (or, if recording failed, in the evidence file).</summary>
    Ran,
    NotFound,

    /// <summary><c>If-Match</c> named another row version. Nothing was claimed.</summary>
    Stale,

    /// <summary>Another attempt holds the claim; <see cref="SavedPostOutcome.AttemptId"/> is that attempt.</summary>
    AlreadyPosting,
    AlreadyPosted,

    /// <summary>Partial, unknown or discarded: locked until a person acts.</summary>
    NotPostable,

    /// <summary>Flagged as a possible duplicate and <c>confirmDuplicate</c> was not sent. Nothing was claimed.</summary>
    DuplicateUnconfirmed,

    /// <summary>Claimed, but the current version could not be bound; recorded as <c>refused</c> and back to pending.</summary>
    Unreadable,
}

/// <summary>
/// What <see cref="SavedRequestPoster.PostAsync"/> did. <see cref="Post"/> is the engine's ending (for
/// <see cref="SavedPostKind.Unreadable"/>, a refused ending naming each JSON path); <see cref="Problem"/> is set
/// instead when the engine threw. <see cref="Status"/> is the request's status after the attempt.
/// </summary>
public sealed record SavedPostOutcome(
    SavedPostKind Kind,
    SyncPost? Post,
    long? AttemptId,
    SavedRequest? Record,
    int? HttpStatus,
    SavedRequestStatus? Status = null,
    ProblemDetails? Problem = null);
