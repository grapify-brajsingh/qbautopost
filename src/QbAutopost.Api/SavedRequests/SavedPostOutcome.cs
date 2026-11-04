using Microsoft.AspNetCore.Mvc;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Companies;
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

    /// <summary>v2 only (dashboard v2 6.2 item 5): every row is marked <c>post: false</c>. Nothing was claimed; no attempt.</summary>
    NothingToPost,

    /// <summary>M17 spec 5.2: the statement has no company. Nothing was claimed; no attempt.</summary>
    CompanyRequired,

    /// <summary>
    /// M17 spec 4.7: the statement's company is not <c>active</c> + <c>approved</c> (or no longer exists, or its file is
    /// outside the root). Nothing was claimed; no attempt; no session. <see cref="SavedPostOutcome.Company"/> is the
    /// company, null when it no longer exists.
    /// </summary>
    CompanyNotReady,
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
    ProblemDetails? Problem = null,
    CompanyContext? Company = null);
