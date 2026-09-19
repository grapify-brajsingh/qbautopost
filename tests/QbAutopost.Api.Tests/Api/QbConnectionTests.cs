using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using Microsoft.Extensions.DependencyInjection;

namespace QbAutopost.Api.Tests.Api;

/// <summary>T-601: which gateway the host uses, and the busy timeout around it (FR-11).</summary>
public sealed class QbConnectionTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static IHostEnvironment Environment(string name) => new HostingEnvironment { EnvironmentName = name };

    private static AppSettings Settings(bool fake) => new() { QuickBooks = new QuickBooksSettings { Fake = fake } };

    [Theory]
    [InlineData(true, true, QbConnectionMode.Simulated)]
    [InlineData(true, false, QbConnectionMode.Simulated)]
    [InlineData(false, true, QbConnectionMode.Sdk)]
    [InlineData(false, false, QbConnectionMode.Unavailable)]
    public void Should_PickGateway_When_FakeAndPlatformGiven(bool fake, bool isWindows, QbConnectionMode expected)
    {
        Assert.Equal(expected, QbConnection.SelectMode(fake, isWindows));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Should_UseSimulatedCompany_When_FakeIsSetOutsideProduction(string environment)
    {
        var connection = QbConnection.Create(Settings(fake: true), Environment(environment));

        Assert.Equal(QbConnectionMode.Simulated, connection.Mode);
        Assert.IsType<SimulatedQbGateway>(connection.Gateway);
    }

    [Fact]
    public void Should_RefuseFake_When_EnvironmentIsProduction()
    {
        Assert.Throws<InvalidOperationException>(() => QbConnection.Create(Settings(fake: true), Environment("Production")));
    }

    [Fact]
    public async Task Should_NeverSendAnything_When_HostIsNotWindowsAndNotFaked()
    {
        var connection = QbConnection.Create(Settings(fake: false), Environment("Production"));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(QbConnectionMode.Sdk, connection.Mode); // constructing the SDK gateway contacts nothing
            return;
        }

        Assert.Equal(QbConnectionMode.Unavailable, connection.Mode);
        await Assert.ThrowsAsync<QuickBooksUnavailableException>(() => connection.Gateway.ProcessAsync("<QBXML/>", CancellationToken.None));
    }

    [Fact]
    public void Should_WrapTheConnectionInBusyTimeout_When_HostResolvesTheGateway()
    {
        var gateway = _factory.Services.GetRequiredService<IQbGateway>();

        var resilient = Assert.IsType<ResilientQbGateway>(gateway);
        var logging = Assert.IsType<LoggingQbGateway>(resilient.Inner);
        Assert.Same(_factory.Gateway, logging.Inner);
    }

    [Fact]
    public async Task Should_EndPartialWithQuickBooksBusy_When_PostingCallHangs()
    {
        var host = _factory.WithSetting("QuickBooks:BusyTimeoutSeconds", "1");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        var folder = _factory.Dir.CopySampleJob();
        Assert.Equal(JobStatus.Ready, (await client.RunToEndAsync(folder)).Status);
        _factory.Gateway.Hang = true;

        using (var post = await client.PostAsync("/jobs/2026-08-tropicana/post", null))
        {
            post.EnsureSuccessStatusCode();
        }

        var view = await client.WaitForJobAsync("2026-08-tropicana");
        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.StartsWith(HoldReasons.QuickBooksBusy, view.Error, StringComparison.Ordinal);
        Assert.Equal(0, view.Counts.Posted);
        Assert.Equal(8, view.Held.Count(h => h.Reason == HoldReasons.QuickBooksBusy));
        var batch = Assert.Single(new LedgerStore(_factory.LedgerFile).Load().Jobs);
        Assert.Equal(0, batch.Posted);
    }
}
