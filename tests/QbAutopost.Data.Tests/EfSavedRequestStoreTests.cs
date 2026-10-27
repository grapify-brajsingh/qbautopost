using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// The store contract on a real PostgreSQL 16, plus what only a database can get wrong: byte-exact text, racing
/// connections, and unique-index races. Every test gets a fresh, migrated database, and the store comes from
/// <see cref="DataServiceCollectionExtensions.AddQbAutopostData"/>, as the app builds it.
/// </summary>
[RequiresPostgres]
public sealed class EfSavedRequestStoreTests(PostgresFixture postgres)
    : SavedRequestStoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        var connectionString = await postgres.CreateDatabaseAsync();
        _services = new ServiceCollection().AddQbAutopostData(connectionString, commandTimeoutSeconds: 30).BuildServiceProvider();
        var factory = _services.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    protected override ISavedRequestStore CreateStore() =>
        _services?.GetRequiredService<ISavedRequestStore>() ?? throw new InvalidOperationException(postgres.SkipReason);

    [PostgresFact]
    public void Should_RegisterEfStoreAsSingleton_When_DataIsAdded()
    {
        var first = CreateStore();

        Assert.IsType<EfSavedRequestStore>(first);
        Assert.Same(first, CreateStore());
    }

    [PostgresFact]
    public async Task Should_ReturnOriginalBodyByteForByte_When_ContainsEmojiAndCjkAnd4000CharMemo()
    {
        var store = CreateStore();
        // 666 × 6 UTF-16 units + 4 = exactly 4000, without cutting the emoji's surrogate pair.
        var memo = string.Concat(Enumerable.Repeat("家賃 🧾 ", 666)) + "末尾です";
        // Odd spacing, a trailing zero and a key order jsonb would not keep: none of it may change in original_body.
        var body = "{ \"transactions\" : [ {\"memo\":\"" + memo + "\", \"payee\":\"Café 🍣 東京\",\"amount\": 10.50,"
            + "\"externalId\":\"e1\",\"kind\":\"check\",\"date\":\"2026-10-01\",\"account\":\"Checking 1234\"} ],\n"
            + "  \"reference\":\"請求-🧾-1\", \"controlTotal\" : 10.50 }";

        Assert.Equal(4000, memo.Length);

        var created = await store.CreateAsync(NewRequest(body), default);
        var read = (await store.GetAsync(created.Record.Id, default))!;

        Assert.Equal(Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(read.OriginalBody));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(), read.OriginalSha256);
        Assert.Equal("請求-🧾-1", read.Reference);
        Assert.Equal(memo, read.CurrentPayload.GetProperty("transactions")[0].GetProperty("memo").GetString());
    }

    [PostgresFact]
    public async Task Should_ClaimOnce_When_TwentyConcurrentClaims()
    {
        var store = CreateStore();
        var r = (await store.CreateAsync(NewRequest(), default)).Record;
        var results = new System.Collections.Concurrent.ConcurrentBag<ClaimResult>();

        await Parallel.ForEachAsync(
            Enumerable.Range(1, 20), new ParallelOptions { MaxDegreeOfParallelism = 20 },
            async (i, ct) => results.Add(await store.TryClaimAsync(r.Id, null, $"user:{i}", false, T0.AddMinutes(1), ct)));

        var winner = Assert.Single(results, c => c.Kind == ClaimKind.Claimed);
        Assert.Equal(19, results.Count(c => c.Kind == ClaimKind.AlreadyPosting));
        Assert.All(results, c => Assert.Equal(winner.AttemptId, c.AttemptId));
        Assert.Equal(winner.AttemptId, Assert.Single(await store.AttemptsAsync(r.Id, default)).Id);
        Assert.Single(await store.EventsAsync(r.Id, default), e => e.Kind == SavedRequestEvents.PostStarted);
        Assert.Equal(r.RowVersion + 1, (await store.GetAsync(r.Id, default))!.RowVersion);
    }

    [PostgresFact]
    public async Task Should_MapUniqueViolationToReplayOrKeyReused_When_ConcurrentSavesShareKey()
    {
        var store = CreateStore();
        var bodies = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? BodyOf() : BodyOf(reference: "OTHER")).ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<(string Body, CreateResult Result)>();

        await Parallel.ForEachAsync(
            bodies, new ParallelOptions { MaxDegreeOfParallelism = 20 },
            async (body, ct) => results.Add((body, await store.CreateAsync(NewRequest(body, key: "same-key"), ct))));

        var created = Assert.Single(results, x => x.Result.Kind == CreateKind.Created);
        Assert.All(results, x => Assert.Equal(created.Result.Record.Id, x.Result.Record.Id));
        Assert.All(
            results.Where(x => x.Result.Kind != CreateKind.Created),
            x => Assert.Equal(x.Body == created.Body ? CreateKind.Replayed : CreateKind.KeyReused, x.Result.Kind));
        Assert.Equal(9, results.Count(x => x.Result.Kind == CreateKind.Replayed));
        Assert.Equal(10, results.Count(x => x.Result.Kind == CreateKind.KeyReused));
        Assert.Single(await store.VersionsAsync(created.Result.Record.Id, default));
    }

    [Fact]
    public void Should_ExposeNoVersionMutation_When_InspectingStoreContract()
    {
        var methods = typeof(ISavedRequestStore).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        // The whole surface, pinned: a new method has to be looked at here before it can ship.
        Assert.Equal(
            new[]
            {
                "AppendVersionAsync", "AttemptAsync", "AttemptsAsync", "ChangeStatusAsync", "CompleteAttemptAsync",
                "CreateAsync", "DuplicateCandidatesAsync", "EventsAsync", "GetAsync", "ListAsync",
                "RecoverInFlightAsync", "ResolveAsync", "SetValidationAsync", "StatsAsync", "TryClaimAsync",
                "VersionAsync", "VersionsAsync",
            },
            methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(methods, m => m.Name.Contains("Update") || m.Name.Contains("Delete") || m.Name.Contains("Remove"));
        // Nothing addresses a version, an event or the original body for writing: only reads take a version number,
        // and no parameter names an event, a version id or the original body.
        Assert.All(
            methods.Where(m => m.GetParameters().Any(p => p.Name == "number")),
            m => Assert.Equal(typeof(Task<SavedRequestVersion?>), m.ReturnType));
        Assert.DoesNotContain(
            methods.SelectMany(m => m.GetParameters()),
            p => p.Name is "versionId" or "eventId" or "originalBody" || p.ParameterType == typeof(SavedRequestVersion)
                || p.ParameterType == typeof(SavedRequestEvent));
    }
}
