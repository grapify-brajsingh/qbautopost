using QbAutopost.Core.Security;

namespace QbAutopost.Core.Tests.Security;

/// <summary>
/// T-1009 (saved-requests spec 11.1, 11.3): who satisfies which scope. People-only scopes are never satisfied by an
/// API key, <c>admin</c> and the legacy key included; <c>qb:read</c> satisfies <c>lists:read</c>; no dashboard role
/// receives a scope that opens a QuickBooks session or the health detail.
/// </summary>
public sealed class ScopeTests
{
    public static TheoryData<string> PeopleOnlyScopes() =>
        [ApiScopes.RequestsEdit, ApiScopes.RequestsPost, ApiScopes.RequestsAdmin, ApiScopes.UsersManage];

    public static TheoryData<string> Roles() => [DashboardRoles.Viewer, DashboardRoles.Editor, DashboardRoles.Admin];

    private static ApiClient Key(params string[] scopes) => new()
    {
        Id = "acme-erp",
        KeyHash = "",
        KeySalt = "",
        Scopes = scopes,
    };

    [Theory]
    [MemberData(nameof(PeopleOnlyScopes))]
    public void Should_RefusePeopleOnlyScopes_When_ApiClientHoldsAdmin(string scope)
    {
        Assert.False(Key(ApiScopes.Admin).Allows(scope));
        Assert.False(Caller.ForClient(Key(ApiScopes.Admin)).Allows(scope));
    }

    [Theory]
    [MemberData(nameof(PeopleOnlyScopes))]
    public void Should_RefusePeopleOnlyScopes_When_ApiClientListsThemExplicitly(string scope) =>
        Assert.False(Caller.ForClient(Key(scope)).Allows(scope));

    [Fact]
    public void Should_DropPeopleOnlyAndSignedIn_When_ClientsJsonListsThemOnAKey()
    {
        var caller = Caller.ForClient(Key(ApiScopes.RequestsPost, ApiScopes.UsersManage, ApiScopes.SignedIn, ApiScopes.QbRead));

        Assert.Equal(new[] { ApiScopes.QbRead }, caller.Scopes);
    }

    [Fact]
    public void Should_GrantListsRead_When_ApiClientHoldsQbRead() =>
        Assert.True(Caller.ForClient(Key(ApiScopes.QbRead)).Allows(ApiScopes.ListsRead));

    [Fact]
    public void Should_NotGrantQbRead_When_ApiClientHoldsListsRead() =>
        Assert.False(Caller.ForClient(Key(ApiScopes.ListsRead)).Allows(ApiScopes.QbRead));

    [Fact]
    public void Should_GrantSubmitAndRead_When_ApiClientHoldsAdmin()
    {
        var admin = Caller.ForClient(Key(ApiScopes.Admin));
        Assert.True(admin.Allows(ApiScopes.RequestsSubmit));
        Assert.True(admin.Allows(ApiScopes.RequestsRead));
    }

    [Fact]
    public void Should_RefuseSignedIn_When_CallerIsAnApiClient() =>
        Assert.False(Caller.ForClient(Key(ApiScopes.Admin)).Allows(ApiScopes.SignedIn));

    [Theory]
    [MemberData(nameof(Roles))]
    public void Should_NotGrantQbReadOrHealthRead_When_AnyDashboardRole(string role)
    {
        var user = Caller.ForUser("u1", "Ann", "ann@example.com", role, false);

        Assert.False(user.Allows(ApiScopes.QbRead));
        Assert.False(user.Allows(ApiScopes.HealthRead));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void Should_GrantNoKeyScope_When_AnyDashboardRole(string role)
    {
        var user = Caller.ForUser("u1", "Ann", null, role, false);

        Assert.DoesNotContain(
            new[] { ApiScopes.QbPost, ApiScopes.QbPostAi, ApiScopes.QbDebug, ApiScopes.JobsRead, ApiScopes.JobsWrite, ApiScopes.RulesWrite, ApiScopes.Admin },
            user.Allows);
    }

    [Fact]
    public void Should_GrantReadAndLists_When_Viewer() =>
        Assert.Equal(
            new[] { ApiScopes.ListsRead, ApiScopes.RequestsRead },
            DashboardRoles.ScopesOf(DashboardRoles.Viewer).Order(StringComparer.Ordinal));

    [Fact]
    public void Should_AddSubmitEditPost_When_Editor() =>
        Assert.Equal(
            new[] { ApiScopes.ListsRead, ApiScopes.RequestsEdit, ApiScopes.RequestsPost, ApiScopes.RequestsRead, ApiScopes.RequestsSubmit },
            DashboardRoles.ScopesOf(DashboardRoles.Editor).Order(StringComparer.Ordinal));

    [Fact]
    public void Should_AddRequestsAdminAndUsersManage_When_Admin() =>
        Assert.Equal(
            new[]
            {
                ApiScopes.ListsRead, ApiScopes.RequestsAdmin, ApiScopes.RequestsEdit, ApiScopes.RequestsPost,
                ApiScopes.RequestsRead, ApiScopes.RequestsSubmit, ApiScopes.UsersManage,
            },
            DashboardRoles.ScopesOf(DashboardRoles.Admin).Order(StringComparer.Ordinal));

    [Fact]
    public void Should_GrantNothing_When_RoleIsUnknown() =>
        Assert.Empty(DashboardRoles.ScopesOf("owner"));

    [Theory]
    [MemberData(nameof(PeopleOnlyScopes))]
    public void Should_GrantPeopleOnlyScope_When_AdminUser(string scope) =>
        Assert.True(Caller.ForUser("u1", "Ann", null, DashboardRoles.Admin, false).Allows(scope));

    [Fact]
    public void Should_GrantSignedIn_When_CallerIsAUser() =>
        Assert.True(Caller.ForUser("u1", "Ann", null, DashboardRoles.Viewer, false).Allows(ApiScopes.SignedIn));

    [Fact]
    public void Should_SatisfyAlternative_When_EitherScopeIsHeld()
    {
        var either = ApiScopes.AnyOf(ApiScopes.RequestsRead, ApiScopes.HealthRead);

        Assert.True(Caller.ForUser("u1", "Ann", null, DashboardRoles.Viewer, false).Allows(either));
        Assert.True(Caller.ForClient(Key(ApiScopes.HealthRead)).Allows(either));
        Assert.False(Caller.ForClient(Key(ApiScopes.QbRead)).Allows(either));
    }

    [Fact]
    public void Should_NameUserById_When_ActorIsRecorded() =>
        Assert.Equal("u1", Caller.ForUser("u1", "Ann", null, DashboardRoles.Viewer, false).Actor);

    [Fact]
    public void Should_PrefixClientId_When_ActorIsRecorded() =>
        Assert.Equal("api-client:acme-erp", Caller.ForClient(Key(ApiScopes.RequestsSubmit)).Actor);
}
