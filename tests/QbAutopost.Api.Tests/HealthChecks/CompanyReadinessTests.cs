using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.Health;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Jobs;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.HealthChecks;

/// <summary>M17 §3, ruling B19: how the company summary is counted, and what readiness does when it cannot be read.</summary>
public sealed class CompanyReadinessTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Company Co(int id, CompanyState state, CompanyAccess access) =>
        new(id, $"{id}_Co", id.ToString(), $"Co {id}", @"D:\QB\Co.qbw", [], state, access, null, null, null, null, [], false,
            DateTime.UnixEpoch, DateTime.UnixEpoch, 1);

    [Theory]
    [InlineData(CompanyState.Ambiguous, CompanyAccess.Unchecked)]
    [InlineData(CompanyState.Missing, CompanyAccess.FileNotFound)]
    [InlineData(CompanyState.Disabled, CompanyAccess.NeedsApproval)]
    [InlineData(CompanyState.Active, CompanyAccess.Approved)]
    public void Should_NotCountAsNotApproved_When_NotActiveOrApproved(CompanyState state, CompanyAccess access)
    {
        var summary = CompanySummary.Of(_dir.Path, [Co(1, state, access)]);

        Assert.Equal(1, summary.Count);
        Assert.Equal(0, summary.NotApproved);
    }

    [Theory]
    [InlineData(CompanyAccess.NeedsApproval)]
    [InlineData(CompanyAccess.Unchecked)]
    [InlineData(CompanyAccess.Error)]
    [InlineData(CompanyAccess.FileNotFound)]
    public void Should_CountAsNotApproved_When_ActiveAndAccessIsNotApproved(CompanyAccess access)
    {
        var summary = CompanySummary.Of(_dir.Path, [Co(1, CompanyState.Active, access), Co(2, CompanyState.Active, CompanyAccess.Approved)]);

        Assert.Equal(2, summary.Count);
        Assert.Equal(1, summary.NotApproved);
    }

    [Fact]
    public void Should_FollowDirectoryExists_When_SettingRootReachable()
    {
        Assert.True(CompanySummary.Of(_dir.Path, []).RootReachable);
        Assert.False(CompanySummary.Of(_dir.Combine("nowhere"), []).RootReachable);
    }

    private sealed class ListStore(Func<CancellationToken, Task<IReadOnlyList<Company>>> list) : ICompanyStore
    {
        public Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct) => list(ct);

        public Task<Company?> GetAsync(int id, CancellationToken ct) => throw new NotSupportedException();

        public Task<DefaultCompanyResult> EnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScanApplied> ApplyScanAsync(ScanPlan plan, string actor, DateTime nowUtc, CancellationToken ct) => throw new NotSupportedException();

        public Task<CompanyChange> UpdateAsync(int id, int expectedRowVersion, CompanyPatch patch, string actor, DateTime nowUtc, CancellationToken ct) => throw new NotSupportedException();

        public Task<CompanyChange> RecordAccessAsync(int id, CompanyAccessCheck check, string actor, CancellationToken ct) => throw new NotSupportedException();

        public Task<DateTime?> LastScanUtcAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<DatabaseGate> ReadyGateAsync()
    {
        var gate = new DatabaseGate(_ => Task.FromResult(new SchemaStatus(true, true, [], null)), true, NullLogger<DatabaseGate>.Instance);
        await gate.RefreshAsync(default);
        Assert.True(gate.IsReady);
        return gate;
    }

    private AppHealth HealthOver(ApiFactory f, DatabaseGate gate, ICompanyStore store)
    {
        var sp = f.Services;
        var options = sp.GetRequiredService<IOptions<AppSettings>>();
        options.Value.Companies.RootFolder = _dir.Path;
        return new AppHealth(
            options, sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<QbConnection>(), sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<IJobStore>(),
            gate, sp.GetRequiredService<IQbListsSource>(), store, NullLogger<AppHealth>.Instance);
    }

    [Fact]
    public async Task Should_AddCompaniesCheck_When_StoreAnswers()
    {
        using var f = new ApiFactory();
        var health = HealthOver(f, await ReadyGateAsync(), new ListStore(_ => Task.FromResult<IReadOnlyList<Company>>([Co(1, CompanyState.Active, CompanyAccess.Unchecked)])));

        var view = await health.ReadyAsync(default);

        Assert.True(view.Ok);
        Assert.Contains(view.Checks, c => c.Name == "companies" && c.Message.EndsWith("1 companies, 1 not approved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_StayReadyWithoutCompaniesCheck_When_StoreThrows()
    {
        using var f = new ApiFactory();
        var health = HealthOver(f, await ReadyGateAsync(), new ListStore(_ => throw new InvalidOperationException("db down")));

        var view = await health.ReadyAsync(default);

        Assert.True(view.Ok);
        Assert.DoesNotContain(view.Checks, c => c.Name == "companies");
    }

    [Fact]
    public async Task Should_StayReadyWithoutCompaniesCheck_When_DatabaseGateIsNotReady()
    {
        using var f = new ApiFactory();
        var notReady = new DatabaseGate(null, true, NullLogger<DatabaseGate>.Instance);
        var health = HealthOver(f, notReady, new ListStore(_ => throw new InvalidOperationException("must not be read")));

        var view = await health.ReadyAsync(default);

        Assert.True(view.Ok);
        Assert.DoesNotContain(view.Checks, c => c.Name == "companies");
    }

    [Fact]
    public async Task Should_Propagate_When_TheTokenIsCancelled()
    {
        using var f = new ApiFactory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var health = HealthOver(f, await ReadyGateAsync(), new ListStore(ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<Company>>([]); }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.ReadyAsync(cts.Token));
    }
}
