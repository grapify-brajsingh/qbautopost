using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Auth;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1115 (dashboard v2 spec 11): <see cref="RefreshTokenService.RotateWithGraceAsync"/> under concurrency, on a real
/// PostgreSQL (as <c>qbautopost_app</c> in <c>QBAUTOPOST_TEST_AS_APP_ROLE=1</c> mode, which proves the app role may
/// set <c>grace_successor_id</c>).
/// </summary>
public sealed class RefreshGraceStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Address = "10.0.0.1";
    private const int Pairs = 12;

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private ServiceProvider? _services;
    private string? _connectionString;
    private string? _userId;

    private RefreshTokenService Service =>
        new(Contexts, Options.Create(new AppSettings()), new FixedClock { UtcNow = DateTime.UtcNow });

    private IDbContextFactory<QbAutopostDbContext> Contexts =>
        _services?.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>() ?? throw new InvalidOperationException(postgres.SkipReason);

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        var connectionString = _connectionString = await postgres.CreateDatabaseAsync();
        _services = new ServiceCollection().AddQbAutopostData(connectionString, commandTimeoutSeconds: 30).BuildServiceProvider();
        await PostgresFixture.MigrateAsync(connectionString);

        await using var db = await Contexts.CreateDbContextAsync();
        var user = new AppUser
        {
            UserName = "ann@example.com",
            NormalizedUserName = "ANN@EXAMPLE.COM",
            DisplayName = "Ann",
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userId = user.Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_connectionString is not null)
        {
            TestDatabase.Release(_connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_IssueExactlyOneSecondSuccessor_When_TwoGraceRefreshesRace()
    {
        for (var pair = 0; pair < Pairs; pair++)
        {
            var first = await Service.IssueAsync(_userId!, null, Address, CancellationToken.None);
            Assert.Equal(RefreshOutcome.Ok, (await Service.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None)).Outcome);

            var results = await Task.WhenAll(
                Task.Run(() => Service.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None)),
                Task.Run(() => Service.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None)));

            Assert.Single(results, r => r.Outcome == RefreshOutcome.Ok);
            Assert.Single(results, r => r.Outcome == RefreshOutcome.ReuseDetected);
            var presented = await TokenAsync(first);
            Assert.NotNull(presented.GraceSuccessorId);
            await using var db = await Contexts.CreateDbContextAsync();
            var family = await db.RefreshTokens.AsNoTracking().Where(t => t.FamilyId == presented.FamilyId).ToListAsync();
            Assert.Equal(3, family.Count);
            Assert.All(family, t => Assert.NotNull(t.RevokedUtc));
        }
    }

    [PostgresFact]
    public async Task Should_RotateAndGrace_When_TwoRefreshesRaceWithALiveToken()
    {
        for (var pair = 0; pair < Pairs; pair++)
        {
            var first = await Service.IssueAsync(_userId!, null, Address, CancellationToken.None);

            var results = await Task.WhenAll(
                Task.Run(() => Service.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None)),
                Task.Run(() => Service.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None)));

            Assert.All(results, r => Assert.Equal(RefreshOutcome.Ok, r.Outcome));
            Assert.NotEqual(results[0].NewToken, results[1].NewToken);
            var presented = await TokenAsync(first);
            Assert.NotNull(presented.ReplacedById);
            Assert.NotNull(presented.GraceSuccessorId);
        }
    }

    [PostgresFact]
    public async Task Should_RevokeFamily_When_AddressUnknown()
    {
        var first = await Service.IssueAsync(_userId!, null, ip: null, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Ok, (await Service.RotateWithGraceAsync(first, null, Grace, CancellationToken.None)).Outcome);

        var reuse = await Service.RotateWithGraceAsync(first, null, Grace, CancellationToken.None);

        Assert.Equal(RefreshOutcome.ReuseDetected, reuse.Outcome);
        Assert.Equal((await TokenAsync(first)).FamilyId, reuse.FamilyId);
    }

    [PostgresFact]
    public async Task Should_LeaveRotateAsyncWithoutGrace_When_V1Rotation()
    {
        var first = await Service.IssueAsync(_userId!, null, Address, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Ok, (await Service.RotateAsync(first, Address, CancellationToken.None)).Outcome);

        var reuse = await Service.RotateAsync(first, Address, CancellationToken.None);

        Assert.Equal(RefreshOutcome.ReuseDetected, reuse.Outcome);
        Assert.Null((await TokenAsync(first)).GraceSuccessorId);
    }

    private async Task<RefreshTokenEntity> TokenAsync(string raw)
    {
        var hash = RefreshTokenService.HashOf(raw);
        await using var db = await Contexts.CreateDbContextAsync();
        return await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenSha256 == hash);
    }
}
