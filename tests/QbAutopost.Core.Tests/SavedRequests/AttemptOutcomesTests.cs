using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

public sealed class AttemptOutcomesTests
{
    private static readonly string[] NoReasons = [];

    [Theory]
    [InlineData(PostEndingKind.Unreadable, AttemptOutcome.Refused, SavedRequestStatus.Pending, 400)]
    [InlineData(PostEndingKind.Refused, AttemptOutcome.Refused, SavedRequestStatus.Pending, 400)]
    [InlineData(PostEndingKind.Held, AttemptOutcome.Held, SavedRequestStatus.Pending, 422)]
    [InlineData(PostEndingKind.Busy, AttemptOutcome.Busy, SavedRequestStatus.Pending, 409)]
    [InlineData(PostEndingKind.Unavailable, AttemptOutcome.Unavailable, SavedRequestStatus.Pending, 503)]
    [InlineData(PostEndingKind.ErrorBeforeSend, AttemptOutcome.Error, SavedRequestStatus.Pending, 500)]
    public void Should_MapEnding_When_KindNeedsNoJobStatus(
        PostEndingKind kind, AttemptOutcome outcome, SavedRequestStatus next, int http) =>
        Assert.Equal(new AttemptVerdict(outcome, next, http), AttemptOutcomes.From(new PostEnding(kind, null, NoReasons)));

    [Fact]
    public void Should_LockAsUnknownWithNoHttpStatus_When_ErrorAfterSend() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, null),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.ErrorAfterSend, null, NoReasons)));

    [Fact]
    public void Should_MoveToPosted_When_CompletedPosted() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Posted, SavedRequestStatus.Posted, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Posted, NoReasons)));

    [Fact]
    public void Should_MoveToPartial_When_CompletedPartial() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Partial, SavedRequestStatus.Partial, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Partial, NoReasons)));

    [Fact]
    public void Should_ReturnToPending_When_CompletedFailedWithNoNoResponseRow() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Failed, SavedRequestStatus.Pending, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Failed, NoReasons)));

    [Fact]
    public void Should_ReturnToPending_When_CompletedFailedWithOnlyOtherHeldReasons() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Failed, SavedRequestStatus.Pending, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Failed, [HoldReasons.UnknownAccount])));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithNoResponseRow() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, 200),
            AttemptOutcomes.From(new PostEnding(
                PostEndingKind.Completed, JobStatus.Failed, [HoldReasons.UnknownAccount, HoldReasons.QuickBooksNoResponse])));

    /// <summary>
    /// SPEC-GAP T-1014: <c>quickbooks-busy</c> on a held row means the write was sent and QuickBooks did not answer
    /// within the busy timeout (DirectPostRunner: "may or may not have been applied"), the same fact as no-response.
    /// </summary>
    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithBusyTimeoutRow() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Failed, [HoldReasons.QuickBooksBusy])));

    [Fact]
    public void Should_StayPartial_When_CompletedPartialWithNoResponseRow() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Partial, SavedRequestStatus.Partial, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Partial, [HoldReasons.QuickBooksNoResponse])));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedWithoutAStatus() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, null, NoReasons)));
}
