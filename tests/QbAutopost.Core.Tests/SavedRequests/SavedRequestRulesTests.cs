using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Core.Tests.SavedRequests;

public sealed class SavedRequestRulesTests
{
    private static readonly HashSet<string> Editor = [ApiScopes.RequestsRead, ApiScopes.RequestsEdit, ApiScopes.RequestsPost];
    private static readonly HashSet<string> Admin = [ApiScopes.RequestsRead, ApiScopes.RequestsEdit, ApiScopes.RequestsPost, ApiScopes.RequestsAdmin];
    private static readonly HashSet<string> Viewer = [ApiScopes.RequestsRead];

    [Fact]
    public void Should_AllowEditValidatePostDiscard_When_PendingAndEditor() =>
        Assert.Equal(
            SavedRequestAction.Edit | SavedRequestAction.Validate | SavedRequestAction.Post | SavedRequestAction.Discard,
            SavedRequestRules.Allowed(SavedRequestStatus.Pending, Editor, true));

    [Fact]
    public void Should_AllowPostOnly_When_PendingAndScopeIsPostOnly() =>
        Assert.Equal(
            SavedRequestAction.Post,
            SavedRequestRules.Allowed(SavedRequestStatus.Pending, new HashSet<string> { ApiScopes.RequestsPost }, true));

    [Theory]
    [InlineData(SavedRequestStatus.Posting)]
    [InlineData(SavedRequestStatus.Posted)]
    [InlineData(SavedRequestStatus.Partial)]
    [InlineData(SavedRequestStatus.Unknown)]
    [InlineData(SavedRequestStatus.Discarded)]
    public void Should_AllowNothing_When_StatusIsLockedAndEditor(SavedRequestStatus status) =>
        Assert.Equal(SavedRequestAction.None, SavedRequestRules.Allowed(status, Editor, true));

    [Theory]
    [InlineData(SavedRequestStatus.Partial)]
    [InlineData(SavedRequestStatus.Unknown)]
    public void Should_AllowOnlyResolve_When_PartialOrUnknownAndAdmin(SavedRequestStatus status) =>
        Assert.Equal(SavedRequestAction.Resolve, SavedRequestRules.Allowed(status, Admin, true));

    [Theory]
    [InlineData(SavedRequestStatus.Posting)]
    [InlineData(SavedRequestStatus.Posted)]
    public void Should_AllowNothing_When_PostingOrPostedAndAdmin(SavedRequestStatus status) =>
        Assert.Equal(SavedRequestAction.None, SavedRequestRules.Allowed(status, Admin, true));

    [Fact]
    public void Should_AllowOnlyRestore_When_DiscardedAndAdmin() =>
        Assert.Equal(SavedRequestAction.Restore, SavedRequestRules.Allowed(SavedRequestStatus.Discarded, Admin, true));

    [Theory]
    [InlineData(SavedRequestStatus.Pending)]
    [InlineData(SavedRequestStatus.Posting)]
    [InlineData(SavedRequestStatus.Posted)]
    [InlineData(SavedRequestStatus.Partial)]
    [InlineData(SavedRequestStatus.Unknown)]
    [InlineData(SavedRequestStatus.Discarded)]
    public void Should_AllowNothing_When_CallerIsApiClientEvenWithScopes(SavedRequestStatus status) =>
        Assert.Equal(SavedRequestAction.None, SavedRequestRules.Allowed(status, Admin, false));

    [Theory]
    [InlineData(SavedRequestStatus.Pending)]
    [InlineData(SavedRequestStatus.Discarded)]
    [InlineData(SavedRequestStatus.Unknown)]
    public void Should_AllowNothing_When_Viewer(SavedRequestStatus status) =>
        Assert.Equal(SavedRequestAction.None, SavedRequestRules.Allowed(status, Viewer, true));

    [Fact]
    public void Should_ListActionNamesInFixedOrder_When_PendingAndEditor() =>
        Assert.Equal(
            ["edit", "validate", "post", "discard"],
            SavedRequestRules.AllowedActionNames(SavedRequestStatus.Pending, Editor, true));

    [Fact]
    public void Should_ListRestore_When_DiscardedAndAdmin() =>
        Assert.Equal(["restore"], SavedRequestRules.AllowedActionNames(SavedRequestStatus.Discarded, Admin, true));

    [Fact]
    public void Should_ListResolve_When_UnknownAndAdmin() =>
        Assert.Equal(["resolve"], SavedRequestRules.AllowedActionNames(SavedRequestStatus.Unknown, Admin, true));

    [Fact]
    public void Should_ListNothing_When_CallerIsApiClient() =>
        Assert.Empty(SavedRequestRules.AllowedActionNames(SavedRequestStatus.Pending, Admin, false));

    [Theory]
    [InlineData(SavedRequestStatus.Unknown, ResolveOutcome.Posted, true)]
    [InlineData(SavedRequestStatus.Unknown, ResolveOutcome.NotPosted, true)]
    [InlineData(SavedRequestStatus.Partial, ResolveOutcome.Posted, true)]
    [InlineData(SavedRequestStatus.Pending, ResolveOutcome.Posted, false)]
    [InlineData(SavedRequestStatus.Posted, ResolveOutcome.NotPosted, false)]
    public void Should_ReportResolvability_When_FromStatusGiven(SavedRequestStatus from, ResolveOutcome to, bool expected) =>
        Assert.Equal(expected, SavedRequestRules.CanResolveTo(from, to));

    [Fact]
    public void Should_RefusePartialToNotPosted() =>
        Assert.False(SavedRequestRules.CanResolveTo(SavedRequestStatus.Partial, ResolveOutcome.NotPosted));
}
