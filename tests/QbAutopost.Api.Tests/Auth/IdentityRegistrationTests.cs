using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>
/// T-1009 (saved-requests spec 4.2, 11.2): Identity is registered only when a database is configured, with a
/// length-only password rule and lockout from <c>Auth:Lockout</c>. Resolving UserManager opens no connection, so the
/// unreachable connection string below is never dialled (rule 1).
/// </summary>
public sealed class IdentityRegistrationTests : IDisposable
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=qbautopost;Username=test";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Should_RegisterIdentityWithLengthOnlyPasswords_When_TheDatabaseIsConfigured()
    {
        // UseSetting, not WithSettings: Program reads these while it registers services, before the test host's
        // app configuration is applied.
        var host = _factory.WithWebHostBuilder(b => b
            .UseSetting("Database:ConnectionString", Unreachable)
            .UseSetting("Auth:Password:MinLength", "14")
            .UseSetting("Auth:Lockout:MaxFailedAttempts", "4")
            .UseSetting("Auth:Lockout:LockoutMinutes", "20"));
        using var scope = host.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var password = users.Options.Password;
        Assert.Equal(14, password.RequiredLength);
        Assert.False(password.RequireDigit || password.RequireLowercase || password.RequireUppercase || password.RequireNonAlphanumeric);
        Assert.Equal(4, users.Options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(20), users.Options.Lockout.DefaultLockoutTimeSpan);
        Assert.True(users.SupportsUserRole);
        Assert.True(users.SupportsUserSecurityStamp);
    }

    [Fact]
    public void Should_NotRegisterUserManager_When_TheDatabaseIsNotConfigured()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<UserManager<AppUser>>());
    }
}
