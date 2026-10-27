using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.Database;

/// <summary>
/// Saved-requests spec 10.6: a request left <c>posting</c> by a crash becomes <c>unknown</c> before the gate is ever
/// Ready, once per process; a recovery that fails keeps the gate not ready and is retried by the next refresh.
/// </summary>
public sealed class InFlightRecoveryTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly SchemaStatus Current = new(true, true, [], null);
    private static readonly SchemaStatus Down = new(false, false, [], SchemaState.Unreachable);

    /// <summary>A saved request claimed for posting and never finished, as a crash would leave it.</summary>
    internal static async Task<SavedRequest> SeedPostingAsync(FakeSavedRequestStore store)
    {
        const string body =
            """{"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        var snapshot = new ValidationSnapshot(ValidationState.Ok, Now, 1, null, [], [], null);
        var created = await store.CreateAsync(
            new NewSavedRequest(
                "api-client:n8n", "api-client:n8n", body, payload, read, PayloadCanonicalizer.Sha256(payload),
                new HashSet<string>(), null, snapshot, new DuplicateMatch(null, []), Now),
            default);
        var claim = await store.TryClaimAsync(created.Record.Id, created.Record.RowVersion, "user:1", false, Now, default);
        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        return claim.Record!;
    }

    private static async Task<SavedRequestStatus> StatusAsync(FakeSavedRequestStore store, long id) =>
        (await store.GetAsync(id, default))!.Status;

    private static DatabaseGate GateFor(
        FakeSavedRequestStore store, Func<SchemaStatus>? schema = null, Action? onRecover = null, Func<DateTime>? now = null) =>
        new(
            _ => Task.FromResult((schema ?? (() => Current))()),
            true,
            NullLogger<DatabaseGate>.Instance,
            ct =>
            {
                onRecover?.Invoke();
                return store.RecoverInFlightAsync(Now, ct);
            },
            now);

    [Fact]
    public async Task Should_MarkPostingRequestsUnknown_When_GateIsCheckedAtStart()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var gate = GateFor(store);

        await new InFlightRecovery(gate).StartAsync(default);

        Assert.True(gate.IsReady);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(store, seeded.Id));
    }

    [Fact]
    public async Task Should_DoNothing_When_DatabaseIsNotConfigured()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var gate = new DatabaseGate(null, true, NullLogger<DatabaseGate>.Instance, ct => store.RecoverInFlightAsync(Now, ct));

        await new InFlightRecovery(gate).StartAsync(default);

        Assert.Equal(SavedRequestStatus.Posting, await StatusAsync(store, seeded.Id));
    }

    [Fact]
    public async Task Should_NotBeReady_When_TheDatabaseIsDownAtStartAndRecoverOnlyWhenItComesUp()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var up = false;
        var gate = GateFor(store, () => up ? Current : Down);

        await new InFlightRecovery(gate).StartAsync(default);
        Assert.False(gate.IsReady);
        Assert.Equal(SavedRequestStatus.Posting, await StatusAsync(store, seeded.Id));

        up = true;
        await gate.RefreshAsync(default);

        Assert.True(gate.IsReady);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(store, seeded.Id));
    }

    [Fact]
    public async Task Should_StayNotReadyAndRetry_When_RecoveryThrows()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var failing = true;
        var gate = new DatabaseGate(
            _ => Task.FromResult(Current),
            true,
            NullLogger<DatabaseGate>.Instance,
            ct => failing ? throw new TimeoutException("Host=db.internal;Password=hunter2") : store.RecoverInFlightAsync(Now, ct));

        await gate.RefreshAsync(default);

        Assert.False(gate.IsReady);
        Assert.Equal("unreachable", gate.Reason);
        Assert.Equal(SavedRequestStatus.Posting, await StatusAsync(store, seeded.Id));

        failing = false;
        await gate.RefreshAsync(default);

        Assert.True(gate.IsReady);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(store, seeded.Id));
    }

    [Fact]
    public async Task Should_LogOnlyTheExceptionType_When_RecoveryFails()
    {
        var log = new ListLogger<DatabaseGate>();
        var gate = new DatabaseGate(
            _ => Task.FromResult(Current),
            true,
            log,
            _ => throw new TimeoutException("Host=db.internal;Password=hunter2"));

        await gate.RefreshAsync(default);

        Assert.Contains("TimeoutException", log.Text);
        Assert.DoesNotContain("db.internal", log.Text);
        Assert.DoesNotContain("hunter2", log.Text);
        Assert.All(log.Entries, e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task Should_RecoverExactlyOnce_When_TheGateFlapsBetweenNotReadyAndReady()
    {
        var store = new FakeSavedRequestStore();
        var recoveries = 0;
        var up = true;
        var gate = GateFor(store, () => up ? Current : Down, () => recoveries++);

        for (var i = 0; i < 3; i++)
        {
            up = true;
            await gate.RefreshAsync(default);
            Assert.True(gate.IsReady);
            up = false;
            await gate.RefreshAsync(default);
            Assert.False(gate.IsReady);
        }

        up = true;
        await gate.RefreshAsync(default);

        Assert.True(gate.IsReady);
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public async Task Should_Answer503_When_ARequestArrivesBeforeRecoveryHasSucceeded()
    {
        var store = new FakeSavedRequestStore();
        var seeded = await SeedPostingAsync(store);
        var gate = new DatabaseGate(
            _ => Task.FromResult(Current),
            true,
            NullLogger<DatabaseGate>.Instance,
            _ => throw new TimeoutException());
        var hits = 0;
        using var host = await new HostBuilder().ConfigureWebHost(web => web
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
                app.UseEndpoints(e => e.MapGroup("/probe").RequireDatabase().MapGet("/", () =>
                {
                    hits++;
                    return Results.Ok();
                }));
            })).StartAsync();

        using var response = await host.GetTestClient().GetAsync("/probe");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, hits);
        Assert.Equal(SavedRequestStatus.Posting, await StatusAsync(store, seeded.Id));
    }
}
