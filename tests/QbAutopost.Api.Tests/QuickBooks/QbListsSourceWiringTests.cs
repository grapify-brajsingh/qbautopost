using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>
/// M14 T-1404 (spec §3.4, §9, §11): which store and source the host wires, and the <c>QuickBooks:ListSync</c> checks.
/// Database mode against a real PostgreSQL is in <c>QbAutopost.Data.Tests</c> (<c>QbListsSourceDbWiringTests</c>).
/// </summary>
public sealed class QbListsSourceWiringTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Should_UseFileSourceAndWriteFile_When_NoConnectionString()
    {
        _factory.Gateway.Company.Accounts.Add(new QbAccount { Name = "Utilities", Type = "Expense" });
        using var client = _factory.CreateAuthorizedClient();

        var source = _factory.Services.GetRequiredService<IQbListsSource>();
        using var response = await client.PostAsync("/qb/sync-lists", null);

        Assert.IsType<FileQbLists>(source);
        Assert.Same(source, _factory.Services.GetRequiredService<IQbListsStore>());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(_factory.Dir.Combine("data", "qb-lists.json")));
        Assert.Contains(source.Current.Accounts, a => a.Name == "Utilities");
    }

    [Fact]
    public void Should_NotRegisterDatabaseSource_When_NoConnectionString()
    {
        Assert.Null(_factory.Services.GetService<DbQbListsSource>());
    }

    [Fact]
    public void Should_DefaultListSyncSettings_When_NothingConfigured()
    {
        var s = _factory.Services.GetRequiredService<IOptions<AppSettings>>().Value.QuickBooks.ListSync;

        Assert.Equal((30, 2, 200), (s.IntervalMinutes, s.FirstDelayMinutes, s.HistoryRows));
    }

    [Fact]
    public void Should_FailStartup_When_IntervalIsBelowFiveMinutes()
    {
        using var host = _factory.WithSetting("QuickBooks:ListSync:IntervalMinutes", "3");

        var ex = Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains("QuickBooks:ListSync:IntervalMinutes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_FailStartup_When_HistoryRowsIsBelowTen()
    {
        using var host = _factory.WithSetting("QuickBooks:ListSync:HistoryRows", "5");

        var ex = Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains("QuickBooks:ListSync:HistoryRows", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Start_When_IntervalIsZero()
    {
        using var host = _factory.WithSetting("QuickBooks:ListSync:IntervalMinutes", "0");

        using var client = host.CreateClient();

        Assert.Equal(0, host.Services.GetRequiredService<IOptions<AppSettings>>().Value.QuickBooks.ListSync.IntervalMinutes);
    }

    [Theory]
    [InlineData(0, 0, 10, true)]
    [InlineData(5, 0, 10, true)]
    [InlineData(30, 2, 200, true)]
    [InlineData(4, 2, 200, false)]
    [InlineData(1, 2, 200, false)]
    [InlineData(-5, 2, 200, false)]
    [InlineData(30, -1, 200, false)]
    [InlineData(30, 2, 9, false)]
    public void Should_ValidateListSyncSettings_When_Checked(int interval, int firstDelay, int history, bool valid)
    {
        var settings = new AppSettings
        {
            QuickBooks = { ListSync = { IntervalMinutes = interval, FirstDelayMinutes = firstDelay, HistoryRows = history } },
        };

        var result = new ListSyncOptionsValidator().Validate(null, settings);

        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public void Should_StartWithEmptyListsAndOneErrorLog_When_DatabaseIsUnreachableAtStartup()
    {
        var logs = new CapturingLoggerProvider();
        using var unreachable = ApiFactory.WithPostgres("Host=127.0.0.1;Port=1;Database=qbautopost;Username=u;Password=p;Timeout=2;Command Timeout=2;Pooling=false");
        using var host = unreachable.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.AddProvider(logs)))));

        using var client = host.CreateClient();
        var source = host.Services.GetRequiredService<DbQbListsSource>();

        Assert.Same(source, host.Services.GetRequiredService<IQbListsSource>());
        Assert.Null(source.Current.SyncedUtc);
        Assert.Empty(source.Current.Accounts);
        var errors = logs.Entries.Where(e => e.Category == typeof(DbQbListsSource).FullName && e.Entry.Level == LogLevel.Error).ToList();
        Assert.True(errors.Count == 1, string.Join("; ", logs.Entries.Where(e => e.Entry.Level >= LogLevel.Warning).Select(e => e.Category + ": " + e.Entry.Message)));
    }
}
