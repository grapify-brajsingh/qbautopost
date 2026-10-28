using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

public sealed class AttemptOutcomesTests
{
    private static readonly PostHeldRow[] NoRows = [];

    private static readonly AttemptVerdict Failed = new(AttemptOutcome.Failed, SavedRequestStatus.Pending, 200);

    private static readonly AttemptVerdict Unknown = new(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, 200);

    private static PostHeldRow Row(string reason, string? note = null) => new(reason, note);

    private static AttemptVerdict FailedWith(params PostHeldRow[] rows) =>
        AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Failed, rows));

    [Theory]
    [InlineData(PostEndingKind.Unreadable, AttemptOutcome.Refused, SavedRequestStatus.Pending, 400)]
    [InlineData(PostEndingKind.Refused, AttemptOutcome.Refused, SavedRequestStatus.Pending, 400)]
    [InlineData(PostEndingKind.Held, AttemptOutcome.Held, SavedRequestStatus.Pending, 422)]
    [InlineData(PostEndingKind.Busy, AttemptOutcome.Busy, SavedRequestStatus.Pending, 409)]
    [InlineData(PostEndingKind.Unavailable, AttemptOutcome.Unavailable, SavedRequestStatus.Pending, 503)]
    [InlineData(PostEndingKind.ErrorBeforeSend, AttemptOutcome.Error, SavedRequestStatus.Pending, 500)]
    public void Should_MapEnding_When_KindNeedsNoJobStatus(
        PostEndingKind kind, AttemptOutcome outcome, SavedRequestStatus next, int http) =>
        Assert.Equal(new AttemptVerdict(outcome, next, http), AttemptOutcomes.From(new PostEnding(kind, null, NoRows)));

    [Fact]
    public void Should_LockAsUnknownWithNoHttpStatus_When_ErrorAfterSend() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, null),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.ErrorAfterSend, null, NoRows)));

    [Fact]
    public void Should_MoveToPosted_When_CompletedPosted() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Posted, SavedRequestStatus.Posted, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Posted, NoRows)));

    [Fact]
    public void Should_MoveToPartial_When_CompletedPartial() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Partial, SavedRequestStatus.Partial, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Partial, NoRows)));

    /// <summary>R31: a failed ending with no held row is unexplained (DirectPostRunner never makes one), so nothing proves it sent nothing.</summary>
    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithNoHeldRow() => Assert.Equal(Unknown, FailedWith(NoRows));

    [Fact]
    public void Should_ReturnToPending_When_CompletedFailedWithOnlyPreSendGateReasons() =>
        Assert.Equal(
            Failed,
            FailedWith(Row(HoldReasons.PossibleDuplicate), Row(HoldReasons.DuplicateCheckFailed), Row(HoldReasons.BackupTooOld)));

    [Fact]
    public void Should_ReturnToPending_When_CompletedFailedWithQuickBooksRejectionWithNonZeroStatus() =>
        Assert.Equal(
            Failed,
            FailedWith(Row(HoldReasons.QuickBooksRejected, PostVerifier.RejectionNote(3140, "invalid reference"))));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithNoResponseRow() =>
        Assert.Equal(Unknown, FailedWith(Row(HoldReasons.UnknownAccount), Row(HoldReasons.QuickBooksNoResponse)));

    /// <summary>
    /// SPEC-GAP T-1014: <c>quickbooks-busy</c> on a held row means the write was sent and QuickBooks did not answer
    /// within the busy timeout (DirectPostRunner: "may or may not have been applied"), the same fact as no-response.
    /// </summary>
    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithBusyTimeoutRow() =>
        Assert.Equal(Unknown, FailedWith(Row(HoldReasons.QuickBooksBusy)));

    /// <summary>R31: QuickBooks holds a transaction under a TxnID with a different amount; G4 matches on amount, so a re-post double-posts.</summary>
    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithAmountMismatchRow() =>
        Assert.Equal(
            Unknown,
            FailedWith(Row(HoldReasons.AmountMismatch, "QuickBooks TxnID 1A-1 echoed 311.41, statement says 311.40; delete it in QuickBooks")));

    /// <summary>R31: status 0 says QuickBooks accepted the add; with no TxnID nobody knows what it created.</summary>
    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithRejectionWithStatusZero() =>
        Assert.Equal(Unknown, FailedWith(Row(HoldReasons.QuickBooksRejected, PostVerifier.NoTxnIdNote)));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithRejectionWithAMissingStatusCode() =>
        Assert.Equal(
            Unknown,
            FailedWith(Row(HoldReasons.QuickBooksRejected, PostVerifier.RejectionNote(-1, null))));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithRejectionThatReturnedATxnId() =>
        Assert.Equal(
            Unknown,
            FailedWith(Row(HoldReasons.QuickBooksRejected, PostVerifier.RejectionNote(3260, "warning", txnId: "1A-1"))));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithAReasonNotInTheAllowList() =>
        Assert.Equal(Unknown, FailedWith(Row(HoldReasons.UnknownAccount), Row("some-new-reason")));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedFailedWithARowWithoutAReason() =>
        Assert.Equal(Unknown, FailedWith(new PostHeldRow(null, null)));

    [Fact]
    public void Should_StayPartial_When_CompletedPartialWithNoResponseRow() =>
        Assert.Equal(
            new AttemptVerdict(AttemptOutcome.Partial, SavedRequestStatus.Partial, 200),
            AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, JobStatus.Partial, [Row(HoldReasons.QuickBooksNoResponse)])));

    [Fact]
    public void Should_LockAsUnknown_When_CompletedWithoutAStatus() =>
        Assert.Equal(Unknown, AttemptOutcomes.From(new PostEnding(PostEndingKind.Completed, null, NoRows)));
}
