using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.Database;

/// <summary>Saved-requests spec 5.5, 10.7: the gate decides, the filter answers 503, nothing polls.</summary>
public sealed class DatabaseGateTests
{
    private static readonly SchemaStatus Current = new(true, true, [], null);
    private static readonly SchemaStatus Down = new(false, false, [], SchemaState.Unreachable);
    private static readonly SchemaStatus Old = new(true, false, ["20261027_Initial"], SchemaState.OutOfDate);

    private static DatabaseGate GateFor(SchemaStatus status, bool require = true) =>
        new(_ => Task.FromResult(status), require, NullLogger<DatabaseGate>.Instance);

    [Fact]
    public void Should_BeNotConfigured_When_NoCheckExists()
    {
        var gate = new DatabaseGate(null, true, NullLogger<DatabaseGate>.Instance);

        Assert.Equal(DatabaseState.NotConfigured, gate.State);
        Assert.Equal("not-configured", gate.Reason);
    }

    [Fact]
    public async Task Should_BeReady_When_SchemaIsCurrent()
    {
        var gate = GateFor(Current);

        await gate.RefreshAsync(default);

        Assert.Equal(DatabaseState.Ready, gate.State);
        Assert.Null(gate.Reason);
    }

    [Fact]
    public async Task Should_BeUnreachable_When_DatabaseCannotBeReached()
    {
        var gate = GateFor(Down);

        await gate.RefreshAsync(default);

        Assert.Equal(DatabaseState.Unreachable, gate.State);
        Assert.Equal("unreachable", gate.Reason);
    }

    [Fact]
    public async Task Should_BeSchemaOutOfDate_When_MigrationsDifferAndSchemaIsRequired()
    {
        var gate = GateFor(Old);

        await gate.RefreshAsync(default);

        Assert.Equal(DatabaseState.SchemaOutOfDate, gate.State);
        Assert.Equal(["20261027_Initial"], gate.Pending);
    }

    [Fact]
    public async Task Should_BeReady_When_MigrationsDifferButSchemaIsNotRequired()
    {
        var gate = GateFor(Old, require: false);

        await gate.RefreshAsync(default);

        Assert.Equal(DatabaseState.Ready, gate.State);
    }

    [Fact]
    public async Task Should_BeUnreachable_When_TheCheckThrows()
    {
        var gate = new DatabaseGate(
            _ => throw new InvalidOperationException("Host=db;Password=secret"), true, NullLogger<DatabaseGate>.Instance);

        await gate.RefreshAsync(default);

        Assert.Equal(DatabaseState.Unreachable, gate.State);
    }

    // ---- request-driven, throttled refresh (R13) ----------------------------------------------------------------

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task Should_SucceedWithoutAnyoneCallingHealth_When_OutageEndedAndLastCheckIsOlderThan10Seconds()
    {
        var clock = new Clock();
        var up = false;
        var gate = new DatabaseGate(
            _ => Task.FromResult(up ? Current : Down), true, NullLogger<DatabaseGate>.Instance, null, () => clock.Now);
        await gate.RefreshAsync(default);
        Assert.False(gate.IsReady);
        var (client, host, hits) = await ProbeAsync(gate);
        using var hostLifetime = host;

        up = true;
        clock.Now = clock.Now.AddSeconds(11);
        using var response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, hits[0]);
    }

    [Fact]
    public async Task Should_NotCheckAgain_When_ASecondRequestComesWithin10Seconds()
    {
        var clock = new Clock();
        var checks = 0;
        var gate = new DatabaseGate(
            _ =>
            {
                checks++;
                return Task.FromResult(Down);
            },
            true,
            NullLogger<DatabaseGate>.Instance,
            null,
            () => clock.Now);
        await gate.RefreshAsync(default);
        var (client, host, _) = await ProbeAsync(gate);
        using var hostLifetime = host;

        clock.Now = clock.Now.AddSeconds(11);
        using var first = await client.GetAsync("/probe");
        clock.Now = clock.Now.AddSeconds(5);
        using var second = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(2, checks); // the explicit one, plus one for the first request only
    }

    [Fact]
    public async Task Should_ShareOneCheck_When_ManyRequestsArriveDuringAnOutage()
    {
        var release = new TaskCompletionSource();
        var checks = 0;
        var gate = new DatabaseGate(
            async _ =>
            {
                Interlocked.Increment(ref checks);
                await release.Task;
                return Down;
            },
            true,
            NullLogger<DatabaseGate>.Instance);

        var callers = Enumerable.Range(0, 8).Select(_ => gate.RefreshSharedAsync(default)).ToList();
        release.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, checks);
    }

    private static async Task<(HttpClient Client, IHost Host, int[] Hits)> ProbeAsync(DatabaseGate gate, Func<Task>? inside = null)
    {
        var hits = new int[1];
        var host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddProblemDetails();
                s.AddSingleton(gate);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e =>
                {
                    var group = e.MapGroup("/probe").RequireDatabase();
                    group.MapGet("/", async () =>
                    {
                        hits[0]++;
                        if (inside is not null)
                        {
                            await inside();
                        }

                        return Results.Ok("reached");
                    });
                });
            })).StartAsync();
        return (host.GetTestClient(), host, hits);
    }

    [Fact]
    public async Task Should_Answer503WithRetryAfter30_When_GateNotReady()
    {
        var gate = GateFor(Current);
        gate.Set(DatabaseState.Unreachable);
        var (client, host, hits) = await ProbeAsync(gate);
        using var hostLifetime = host;

        using var response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Database unavailable", problem.GetProperty("title").GetString());
        Assert.Equal(0, hits[0]);
    }

    [Fact]
    public async Task Should_ReachTheRoute_When_GateIsReady()
    {
        var gate = GateFor(Current);
        gate.Set(DatabaseState.Ready);
        var (client, host, hits) = await ProbeAsync(gate);
        using var hostLifetime = host;

        using var response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, hits[0]);
    }

    [Fact]
    public async Task Should_RecheckAndAnswer503_When_AStoreCallThrowsNpgsqlException()
    {
        var checks = 0;
        var gate = new DatabaseGate(
            _ =>
            {
                checks++;
                return Task.FromResult(Down);
            },
            true,
            NullLogger<DatabaseGate>.Instance);
        gate.Set(DatabaseState.Ready);
        var (client, host, _) = await ProbeAsync(gate, () => throw new NpgsqlException("connection refused"));
        using var hostLifetime = host;

        using var response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, checks);
        Assert.Equal(DatabaseState.Unreachable, gate.State);
    }

    [Fact]
    public async Task Should_RecheckAndAnswer503_When_AStoreCallTimesOutInsideAWrapper()
    {
        var gate = GateFor(Down);
        gate.Set(DatabaseState.Ready);
        var (client, host, _) = await ProbeAsync(
            gate, () => throw new InvalidOperationException("retry failed", new TimeoutException()));
        using var hostLifetime = host;

        using var response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(DatabaseState.Unreachable, gate.State);
    }

    [Fact]
    public async Task Should_NotRecheck_When_TheGateIsNotReadyAndNoOneAsked()
    {
        var checks = 0;
        var gate = new DatabaseGate(
            _ =>
            {
                checks++;
                return Task.FromResult(Current);
            },
            true,
            NullLogger<DatabaseGate>.Instance);
        gate.Set(DatabaseState.Unreachable);
        var (client, host, _) = await ProbeAsync(gate);
        using var hostLifetime = host;

        await client.GetAsync("/probe");
        await client.GetAsync("/probe");

        Assert.Equal(0, checks);
    }
}
