using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using QbAutopost.Api.Auth;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>
/// T-1009 (saved-requests spec 10.11): the real <see cref="UserGate"/> over a real <see cref="UserManager{TUser}"/>,
/// with an in-memory Identity store in place of PostgreSQL (rule 1). A disabled user keeps access for at most 30 s.
/// </summary>
public sealed class UserGateTests : IDisposable
{
    private readonly FixedClock _clock = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly InMemoryUserStore _store = new();
    private readonly UserManager<AppUser> _users;
    private readonly UserGate _gate;

    public UserGateTests()
    {
        _users = new UserManager<AppUser>(
            _store, null!, null!, null!, null!, null!, null!, null!, NullLogger<UserManager<AppUser>>.Instance);
        _gate = new UserGate(_users, _cache, _clock);
    }

    public void Dispose()
    {
        _users.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task Should_RejectUser_Within30Seconds_When_Disabled()
    {
        var user = _store.Add("ann", DashboardRoles.Editor);
        var token = Principal(user);
        Assert.NotNull(await _gate.ResolveAsync(token, default));

        user.IsDisabled = true;
        _clock.UtcNow += TimeSpan.FromSeconds(29);
        Assert.NotNull(await _gate.ResolveAsync(token, default));

        _clock.UtcNow += TimeSpan.FromSeconds(2);
        Assert.Null(await _gate.ResolveAsync(token, default));
    }

    [Fact]
    public async Task Should_GiveTheScopesOfTheRole_When_TheUserIsResolved()
    {
        var user = _store.Add("ann", DashboardRoles.Viewer);

        var caller = await _gate.ResolveAsync(Principal(user), default);

        Assert.NotNull(caller);
        Assert.Equal(CallerKind.User, caller.Kind);
        Assert.Equal(user.Id, caller.Id);
        Assert.Equal(DashboardRoles.Viewer, caller.Role);
        Assert.Equal(DashboardRoles.ScopesOf(DashboardRoles.Viewer), caller.Scopes);
    }

    [Fact]
    public async Task Should_RejectTheOldToken_When_TheStampChanged()
    {
        var user = _store.Add("ann", DashboardRoles.Editor);
        var old = Principal(user);
        Assert.NotNull(await _gate.ResolveAsync(old, default));

        user.SecurityStamp = "new-stamp";
        _clock.UtcNow += TimeSpan.FromSeconds(31);

        Assert.Null(await _gate.ResolveAsync(old, default));
    }

    [Fact]
    public async Task Should_AcceptTheNewToken_When_TheStampChangedWhileCached()
    {
        var user = _store.Add("ann", DashboardRoles.Editor);
        Assert.NotNull(await _gate.ResolveAsync(Principal(user), default));

        user.SecurityStamp = "new-stamp";

        Assert.NotNull(await _gate.ResolveAsync(Principal(user), default));
    }

    [Fact]
    public async Task Should_NotCacheTheOldSnapshot_When_ForgetRunsWhileItIsBeingLoaded()
    {
        var user = _store.Add("ann", DashboardRoles.Editor);
        var token = Principal(user);
        // The lookup has read the enabled user; then the admin's change commits and Forget runs; then the lookup caches.
        _store.AfterFind = () =>
        {
            UserGate.Forget(_cache, user.Id);
            _store.AfterFind = null;
        };
        Assert.NotNull(await _gate.ResolveAsync(token, default));

        user.IsDisabled = true;

        Assert.Null(await _gate.ResolveAsync(token, default));
    }

    [Fact]
    public async Task Should_GiveNoScopes_When_TheUserHasNoRole()
    {
        var user = _store.Add("ann", role: null);

        var caller = await _gate.ResolveAsync(Principal(user), default);

        Assert.NotNull(caller);
        Assert.Empty(caller.Scopes);
    }

    [Fact]
    public async Task Should_RejectToken_When_TheUserIsUnknown() =>
        Assert.Null(await _gate.ResolveAsync(Principal(new AppUser { Id = "ghost", SecurityStamp = "s" }), default));

    [Fact]
    public async Task Should_RejectToken_When_ItCarriesNoStamp()
    {
        var user = _store.Add("ann", DashboardRoles.Admin);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AccessTokens.Subject, user.Id)], "test"));

        Assert.Null(await _gate.ResolveAsync(principal, default));
    }

    private static ClaimsPrincipal Principal(AppUser user) =>
        new(new ClaimsIdentity(
            [new Claim(AccessTokens.Subject, user.Id), new Claim(AccessTokens.Stamp, user.SecurityStamp!)], "test"));

    /// <summary>Just enough of an Identity store for <see cref="UserGate"/>: find by id, roles and the security stamp.</summary>
    private sealed class InMemoryUserStore : IUserRoleStore<AppUser>, IUserSecurityStampStore<AppUser>
    {
        private readonly Dictionary<string, AppUser> _users = [];
        private readonly Dictionary<string, List<string>> _roles = [];

        public AppUser Add(string name, string? role)
        {
            var user = new AppUser
            {
                Id = Guid.NewGuid().ToString("N"),
                UserName = name,
                Email = name + "@example.com",
                DisplayName = name,
                SecurityStamp = Guid.NewGuid().ToString("N"),
            };
            _users[user.Id] = user;
            _roles[user.Id] = role is null ? [] : [role];
            return user;
        }

        /// <summary>Runs after a lookup has read the user, before it returns: a change and its Forget landing mid-load.</summary>
        public Action? AfterFind { get; set; }

        public Task<AppUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        {
            var found = _users.GetValueOrDefault(userId);
            AfterFind?.Invoke();
            return Task.FromResult(found);
        }

        public Task<IList<string>> GetRolesAsync(AppUser user, CancellationToken cancellationToken) =>
            Task.FromResult<IList<string>>(_roles.GetValueOrDefault(user.Id) ?? []);

        public Task<string?> GetSecurityStampAsync(AppUser user, CancellationToken cancellationToken) =>
            Task.FromResult(user.SecurityStamp);

        public Task SetSecurityStampAsync(AppUser user, string stamp, CancellationToken cancellationToken)
        {
            user.SecurityStamp = stamp;
            return Task.CompletedTask;
        }

        public Task<string> GetUserIdAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

        public Task<string?> GetUserNameAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);

        public Task<bool> IsInRoleAsync(AppUser user, string roleName, CancellationToken cancellationToken) =>
            Task.FromResult(_roles.GetValueOrDefault(user.Id)?.Contains(roleName) == true);

        public void Dispose()
        {
        }

        // Not used by UserGate.
        public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(AppUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddToRoleAsync(AppUser user, string roleName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveFromRoleAsync(AppUser user, string roleName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IList<AppUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
