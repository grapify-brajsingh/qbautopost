using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Logging;

/// <summary>M14 §11: the <c>QuickBooks:</c> startup line states the list-sync schedule and where the lists live.</summary>
public sealed class StartupLogTests
{
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "QbAutopost";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string QuickBooksLine(AppSettings s)
    {
        var log = new ListLogger<StartupLogTests>();
        StartupLog.Write(log, s, new Env());
        return Assert.Single(log.Entries, e => e.Message.StartsWith("QuickBooks:", StringComparison.Ordinal)).Message;
    }

    [Fact]
    public void Should_NameIntervalAndFirstDelay_When_ScheduleIsOn()
    {
        var s = new AppSettings { QuickBooks = { ListSync = { IntervalMinutes = 30, FirstDelayMinutes = 2 } } };

        Assert.Contains("list sync every 30 min (first after 2 min)", QuickBooksLine(s), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_SayScheduleOff_When_IntervalIsZero()
    {
        var s = new AppSettings { QuickBooks = { ListSync = { IntervalMinutes = 0 } } };

        var line = QuickBooksLine(s);

        Assert.Contains("list sync schedule off", line, StringComparison.Ordinal);
        Assert.DoesNotContain("list sync every", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NameTheFile_When_NoDatabaseIsConfigured()
    {
        var s = new AppSettings { Paths = { QbLists = "data/qb-lists.json" } };

        Assert.Contains("lists in data/qb-lists.json", QuickBooksLine(s), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_SayDatabase_When_ConnectionStringIsSet()
    {
        var s = new AppSettings { Database = { ConnectionString = "Host=db;Password=hunter2" } };

        var line = QuickBooksLine(s);

        Assert.Contains("lists in the database", line, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
    }
}
