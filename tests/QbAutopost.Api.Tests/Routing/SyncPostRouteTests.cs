using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Security;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Routing;

/// <summary>
/// T-924: the three lists that decide what <c>/quickbooks/transactions/validate-and-post</c> is allowed to be.
/// <para>
/// Two of them fail <i>open</i> for this path if nobody names it, which is why they are pinned here rather than
/// left to the end-to-end tests. <see cref="ApiRoutes.ScopeFor"/> would resolve it to <c>qb:read</c> through the
/// <c>/quickbooks/</c> fallback, and <see cref="ApiRoutes.IsIdempotent"/> would leave it without replay protection
/// <i>and</i> in the cheap rate-limit bucket. Only <see cref="ApiRoutes.IsMutating"/> is right by default, by the
/// route not being in the read-only list. A single wrong entry in any of them is silent.
/// </para>
/// </summary>
public sealed class SyncPostRouteTests
{
    private const string Flat = ApiRoutes.SyncPostRoute;
    private const string Versioned = ApiRoutes.V1Prefix + ApiRoutes.SyncPostRoute;
    private const string Validate = ApiRoutes.V1Prefix + "/quickbooks/transactions/validate";

    [Theory]
    [InlineData(Flat)]
    [InlineData(Versioned)]
    public void Should_RequireQbPost_When_TheRouteIsTheSynchronousPost(string path) =>
        Assert.Equal(ApiScopes.QbPost, ApiRoutes.ScopeFor("POST", path));

    /// <summary>
    /// The trap, stated as a fact rather than an intention: this path is <b>not</b> the one the exact-match branch
    /// was originally written for, and everything under <c>/quickbooks/</c> that nobody names resolves to the read
    /// scope. A caller issued <c>qb:read</c> for looking at lists would have been able to post money.
    /// </summary>
    [Fact]
    public void Should_NotRequireOnlyQbRead_When_TheRouteWouldFallThroughToTheQuickBooksBranch()
    {
        Assert.NotEqual("/quickbooks/transactions", ApiRoutes.SyncPostRoute);
        Assert.StartsWith("/quickbooks/", ApiRoutes.SyncPostRoute, StringComparison.Ordinal);
        Assert.NotEqual(ApiScopes.QbRead, ApiRoutes.ScopeFor("POST", Versioned));
    }

    /// <summary>Its read-only sibling keeps the read scope: adding this route must not widen that one.</summary>
    [Fact]
    public void Should_StillRequireOnlyQbRead_When_TheRouteIsTheOfflineValidate() =>
        Assert.Equal(ApiScopes.QbRead, ApiRoutes.ScopeFor("POST", Validate));

    /// <summary>
    /// It changes something, so it is audited (FR-A-16) and is not in <c>ReadOnlyPosts</c>. The name ends in "post"
    /// and contains "validate": a shortcut matching either word would have filed one of the two the wrong way.
    /// </summary>
    [Theory]
    [InlineData(Flat)]
    [InlineData(Versioned)]
    public void Should_BeMutating_When_TheRouteIsTheSynchronousPost(string path) =>
        Assert.True(ApiRoutes.IsMutating("POST", path));

    [Fact]
    public void Should_StillBeReadOnly_When_TheRouteIsTheOfflineValidate() =>
        Assert.False(ApiRoutes.IsMutating("POST", Validate));

    /// <summary>
    /// §3 and §8: <c>Idempotency-Key</c> is honoured here, because after a lost response it is the only thing that
    /// returns the original transaction ids. The same predicate puts the route in the posting rate-limit bucket.
    /// </summary>
    [Theory]
    [InlineData(Flat)]
    [InlineData(Versioned)]
    public void Should_HonourAnIdempotencyKey_When_TheRouteIsTheSynchronousPost(string path) =>
        Assert.True(ApiRoutes.IsIdempotent("POST", path));

    [Fact]
    public void Should_DrawOnThePostingBudget_When_TheRouteIsTheSynchronousPost() =>
        Assert.Equal(RateLimitBucket.Post, RateLimitPolicy.BucketFor("POST", Versioned));
}
