using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Store;

/// <summary>
/// T-909 / FR-A-12: <c>Idempotency-Key</c>. A caller whose connection dropped must be able to retry without posting
/// twice — and must be told plainly when they have reused one key for two different requests.
/// <para>
/// This is the convenience layer, not the safety layer: G4 and the ledger still catch a repeat sent without a key.
/// That is why a claim can be released when nothing happened, rather than locking a key forever.
/// </para>
/// </summary>
public sealed class IdempotencyStoreTests : IDisposable
{
    private const string Route = "POST /api/v1/quickbooks/transactions";
    private static readonly DateTime Now = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);

    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private string File => Path.Combine(_dir.Root, "idempotency.json");

    private IdempotencyStore Store(int retentionDays = 30) => new(File, retentionDays);

    [Fact]
    public void Should_Claim_When_TheKeyIsNew()
    {
        Assert.Equal(IdempotencyVerdict.Claimed, Store().Claim("k1", Route, "hash-a", null, Now).Verdict);
    }

    [Fact]
    public void Should_SayInProgress_When_TheSameRequestIsStillRunning()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", null, Now);

        // The caller retried before the first answer arrived; telling them to wait is the only safe answer.
        Assert.Equal(IdempotencyVerdict.InProgress, store.Claim("k1", Route, "hash-a", null, Now).Verdict);
    }

    [Fact]
    public void Should_Replay_When_TheSameRequestArrivesAgainAfterItFinished()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", null, Now);
        store.Complete("k1", Route, 200, "{\"batchId\":\"api-x#1\"}", "application/json", "api-x#1", Now);

        var claim = store.Claim("k1", Route, "hash-a", null, Now);

        Assert.Equal(IdempotencyVerdict.Replay, claim.Verdict);
        Assert.Equal("{\"batchId\":\"api-x#1\"}", claim.Entry!.Response);
        Assert.Equal(200, claim.Entry.StatusCode);
        Assert.Equal("api-x#1", claim.Entry.BatchId);
    }

    [Fact]
    public void Should_Conflict_When_OneKeyIsUsedForTwoDifferentBodies()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", null, Now);
        store.Complete("k1", Route, 200, "{}", "application/json", "api-x#1", Now);

        // Replaying the first answer would be a lie about the second request; refusing is the only honest option.
        Assert.Equal(IdempotencyVerdict.Conflict, store.Claim("k1", Route, "hash-b", null, Now).Verdict);
    }

    [Fact]
    public void Should_Conflict_When_ADifferentBodyArrivesWhileTheFirstRuns()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", null, Now);

        Assert.Equal(IdempotencyVerdict.Conflict, store.Claim("k1", Route, "hash-b", null, Now).Verdict);
    }

    [Fact]
    public void Should_ClaimAgain_When_TheFirstAttemptWasReleased()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", null, Now);

        // Nothing happened — the request was refused before it did any work — so the key is free again.
        store.Release("k1", Route);

        Assert.Equal(IdempotencyVerdict.Claimed, store.Claim("k1", Route, "hash-a", null, Now).Verdict);
    }

    [Fact]
    public void Should_KeepKeysApart_When_TheSameKeyIsUsedOnTwoRoutes()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", null, Now);

        // A caller numbering their requests 1, 2, 3 must not have an undo collide with a post.
        Assert.Equal(IdempotencyVerdict.Claimed, store.Claim("k1", "POST /api/v1/batches/x/undo", "hash-a", null, Now).Verdict);
    }

    [Fact]
    public void Should_KeepKeysApart_When_TwoClientsUseTheSameKey()
    {
        var store = Store();
        store.Claim("k1", Route, "hash-a", "client-a", Now);

        Assert.Equal(IdempotencyVerdict.Claimed, store.Claim("k1", Route, "hash-a", "client-b", Now).Verdict);
    }

    [Fact]
    public void Should_ForgetTheKey_When_ItIsOlderThanTheRetention()
    {
        var store = Store(retentionDays: 30);
        store.Claim("k1", Route, "hash-a", null, Now);
        store.Complete("k1", Route, 200, "{}", "application/json", "api-x#1", Now);

        var claim = store.Claim("k1", Route, "hash-a", null, Now.AddDays(31));

        Assert.Equal(IdempotencyVerdict.Claimed, claim.Verdict);
    }

    [Fact]
    public void Should_SurviveARestart_When_TheStoreIsReopened()
    {
        Store().Claim("k1", Route, "hash-a", null, Now);
        Store().Complete("k1", Route, 200, "{\"ok\":true}", "application/json", "api-x#1", Now);

        var claim = Store().Claim("k1", Route, "hash-a", null, Now);

        Assert.Equal(IdempotencyVerdict.Replay, claim.Verdict);
        Assert.Equal("{\"ok\":true}", claim.Entry!.Response);
    }

    [Fact]
    public void Should_StartEmpty_When_TheFileIsUnreadable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ this is not json");

        // A corrupt bookkeeping file must not take the API down; the worst case is one un-deduplicated retry,
        // which G4 still catches.
        Assert.Equal(IdempotencyVerdict.Claimed, Store().Claim("k1", Route, "hash-a", null, Now).Verdict);
    }

    [Fact]
    public void Should_HashTheBody_When_TwoBodiesDiffer()
    {
        var a = IdempotencyStore.HashOf("{\"amount\":1.00}"u8);
        var b = IdempotencyStore.HashOf("{\"amount\":2.00}"u8);

        Assert.NotEqual(a, b);
        Assert.Equal(a, IdempotencyStore.HashOf("{\"amount\":1.00}"u8));
        Assert.Equal(64, a.Length);
    }
}
