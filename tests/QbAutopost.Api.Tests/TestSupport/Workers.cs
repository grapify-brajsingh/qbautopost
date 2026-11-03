using Microsoft.Extensions.Logging.Abstractions;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>Builds a <see cref="JobWorker"/> for unit tests, over fakes and files in <c>folder</c>.</summary>
public static class Workers
{
    /// <summary>M14: the list sync schedule off (<c>IntervalMinutes = 0</c>), i.e. exactly the pre-M14 loop.</summary>
    public static JobWorker ScheduleOff(JobQueue queue, IJobProcessor processor, IJobStore store, string folder)
    {
        var clock = new FixedClock();
        var lists = new FileQbLists(Path.Combine(folder, "qb-lists.json"));
        return new JobWorker(
            queue,
            processor,
            store,
            NullLogger<JobWorker>.Instance,
            new ListSyncSchedule(new ListSyncSettings { IntervalMinutes = 0 }, lists, clock),
            Sync(folder, new FakeQbGateway(), clock, lists),
            new FakeQbPresence());
    }

    /// <summary>A real <see cref="QbListSync"/> over the sample rules, writing its files under <paramref name="folder"/>.</summary>
    public static QbListSync Sync(string folder, IQbGateway gateway, IClock clock, IQbListsStore lists) => new(
        new PipelineOptions
        {
            CompanyName = ApiFactory.Company,
            RulesFile = Fixtures.SampleRules,
            LedgerFile = Path.Combine(folder, "ledger.json"),
            QbListsFile = Path.Combine(folder, "qb-lists.json"),
        },
        gateway,
        clock,
        lists,
        (IQbListsSource)lists);
}
