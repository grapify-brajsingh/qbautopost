using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>
    /// Ruling R11: a grace refresh holds the successor's lock and has inserted its second successor G (uncommitted);
    /// a reuse with no grace (unknown address) revokes the family and waits on that lock; the grace commits. The
    /// revocation must still revoke G, which its first UPDATE could not see.
    /// </summary>
    [PostgresFact]
    public async Task Should_RevokeGraceSuccessor_When_FamilyRevokedConcurrently()
    {
        var first = await Service.IssueAsync(_userId!, null, Address, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Ok, (await Service.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None)).Outcome);
        var hold = new HoldBeforeGraceRecord();
        await using var held = new ServiceCollection()
            .AddPooledDbContextFactory<QbAutopostDbContext>(o => o.UseNpgsql(_connectionString!).AddInterceptors(hold))
            .BuildServiceProvider();
        var holding = new RefreshTokenService(
            held.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>(), Options.Create(new AppSettings()), new FixedClock { UtcNow = DateTime.UtcNow });

        var grace = Task.Run(() => holding.RotateWithGraceAsync(first, Address, Grace, CancellationToken.None));
        await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var theft = Task.Run(() => Service.RotateWithGraceAsync(first, ip: null, Grace, CancellationToken.None));
        await WaitForALockWaiterAsync();
        hold.Release.SetResult();

        Assert.Equal(RefreshOutcome.Ok, (await grace).Outcome);
        Assert.Equal(RefreshOutcome.ReuseDetected, (await theft).Outcome);
        var presented = await TokenAsync(first);
        Assert.NotNull(presented.GraceSuccessorId);
        await using var db = await Contexts.CreateDbContextAsync();
        var family = await db.RefreshTokens.AsNoTracking().Where(t => t.FamilyId == presented.FamilyId).ToListAsync();
        Assert.Equal(3, family.Count);
        Assert.All(family, t => Assert.NotNull(t.RevokedUtc));
    }

    /// <summary>Until another session of this database waits on a lock (the revocation, blocked on the successor's row).</summary>
    private async Task WaitForALockWaiterAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var db = await Contexts.CreateDbContextAsync();
            var waiting = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                .SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "the revocation never waited on the successor's lock");
            await Task.Delay(20);
        }
    }

    /// <summary>Holds the grace transaction open just before it records <c>grace_successor_id</c>, once.</summary>
    private sealed class HoldBeforeGraceRecord : DbCommandInterceptor
    {
        private int _fired;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("grace_successor_id", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                Reached.SetResult();
                await Release.Task;
            }

            return result;
        }
    }

    private async Task<RefreshTokenEntity> TokenAsync(string raw)
    {
        var hash = RefreshTokenService.HashOf(raw);
        await using var db = await Contexts.CreateDbContextAsync();
        return await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenSha256 == hash);
    }
}
