using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Tests.Jobs;

/// <summary>T-1709 (M17 spec 4.1, 4.7, A-12): which companies a scheduled sweep syncs, per <c>Companies:ScheduledSync</c>.</summary>
public sealed class CompanySweepTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private static Company CompanyOf(int id, CompanyState state = CompanyState.Active, CompanyAccess access = CompanyAccess.Approved) =>
        new(id, null, null, $"Company {id}", $@"C:\fake\{id}.QBW", [], state, access, T0, null, null, null, [], id == 1, T0, T0, 1);

    private static ListSyncRun Synced() => new()
    {
        Trigger = ListSyncTrigger.Schedule, StartedUtc = T0, FinishedUtc = T0, Outcome = ListSyncOutcome.Ok,
    };

    private static IReadOnlyList<int> Ids(IReadOnlyList<Company> selected) => selected.Select(c => c.Id).ToList();

    [Fact]
    public void Should_SelectWaitingOrNeverSynced_When_ModeIsWaitingOnly()
    {
        // 1: synced, nothing waiting. 2: synced, statements waiting. 3: never synced.
        var companies = new[] { CompanyOf(1), CompanyOf(2), CompanyOf(3) };
        var pending = new Dictionary<int, int> { [2] = 4 };

        var selected = CompanySweep.Select(companies, ScheduledSyncModes.WaitingOnly, pending, id => id == 3 ? null : Synced());

        Assert.Equal([2, 3], Ids(selected));
    }

    [Fact]
    public void Should_SelectEveryApprovedCompany_When_ModeIsAll()
    {
        var companies = new[] { CompanyOf(1), CompanyOf(2), CompanyOf(3) };

        var selected = CompanySweep.Select(companies, ScheduledSyncModes.All, new Dictionary<int, int>(), _ => Synced());

        Assert.Equal([1, 2, 3], Ids(selected));
    }

    [Fact]
    public void Should_SelectNothing_When_ModeIsOff()
    {
        var companies = new[] { CompanyOf(1), CompanyOf(2) };

        var selected = CompanySweep.Select(companies, ScheduledSyncModes.Off, new Dictionary<int, int> { [1] = 1, [2] = 1 }, _ => null);

        Assert.Empty(selected);
    }

    [Fact]
    public void Should_NeverSelectUnapprovedOrInactive_When_AnyMode()
    {
        var companies = new[]
        {
            CompanyOf(1),
            CompanyOf(2, access: CompanyAccess.NeedsApproval),
            CompanyOf(3, access: CompanyAccess.Unchecked),
            CompanyOf(4, state: CompanyState.Missing),
            CompanyOf(5, state: CompanyState.Disabled),
            CompanyOf(6, state: CompanyState.Ambiguous),
        };
        var pending = companies.ToDictionary(c => c.Id, _ => 1);

        foreach (var mode in new[] { ScheduledSyncModes.WaitingOnly, ScheduledSyncModes.All })
        {
            Assert.Equal([1], Ids(CompanySweep.Select(companies, mode, pending, _ => null)));
        }
    }
}
