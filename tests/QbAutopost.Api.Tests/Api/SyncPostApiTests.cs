using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-924 / ADR-0008 / <c>docs/validate-and-post-transaction-data.md</c> VP-1…VP-10:
/// <c>POST /api/v1/quickbooks/transactions/validate-and-post</c> — validate the whole payload, then post all of it
/// or none of it, in one request and one response.
/// <para>
/// Two assertions carry this feature. <b>The gateway receives nothing</b> when any row is held (VP-2) — that is the
/// all-or-nothing gate, and a version of this route that posted the other rows anyway would still pass every other
/// test here. And <b>a <c>qb:read</c> key is refused</b>: the scope fallback for anything under
/// <c>/quickbooks/</c> is the read scope, so without the branch in <c>ApiRoutes.ScopeFor</c> this route would move
/// money for a key issued to look at lists — silently, with a green suite.
/// </para>
/// </summary>
public sealed class SyncPostApiTests : IDisposable
{
    private const string Route = "/api/v1/quickbooks/transactions/validate-and-post";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string BatchesRoot => _factory.Dir.Combine("data", "api-batches");

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string fixture, WebApplicationFactory<Program>? host = null, string? idempotencyKey = null)
    {
        using var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        if (idempotencyKey is not null)
        {
            client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await client.PostAsync(Route, SyncPostFixtures.Content(fixture));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    // VP-3
    [Fact]
    public async Task Should_PostEveryRowAndReturnTheTxnIds_When_NothingIsHeld()
    {
        var (status, body) = await PostAsync(SyncPostFixtures.Posted);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("counts").GetProperty("submitted").GetInt32());
        Assert.Equal(2, body.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(283.42m, body.GetProperty("totals").GetProperty("posted").GetDecimal());
        var posted = body.GetProperty("posted").EnumerateArray().ToList();
        Assert.All(posted, row => Assert.StartsWith("FAKE-", row.GetProperty("txnId").GetString()));
        Assert.Equal(["row-1", "row-2"], posted.Select(r => r.GetProperty("externalId").GetString()));
    }

    /// <summary>
    /// VP-2, and the heart of the feature. One unresolvable payee among many refuses the <b>whole</b> request: the
    /// answer is 422 naming it, and QuickBooks is not contacted at all — not even for G4's duplicate queries.
    /// </summary>
    [Fact]
    public async Task Should_PostNothingAtAll_When_OneRowOfManyWouldBeHeld()
    {
        var (status, body) = await PostAsync(SyncPostFixtures.Held);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Empty(new LedgerStore(_factory.LedgerFile).Load().Posted);
        Assert.Equal(3, body.GetProperty("counts").GetProperty("submitted").GetInt32());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("held").GetInt32());
        // The two good rows are counted as postable, so the cost of the refusal is visible to whoever reads it.
        Assert.Equal(2, body.GetProperty("counts").GetProperty("postable").GetInt32());
        var finding = body.GetProperty("validation").EnumerateArray().Single();
        Assert.Equal("row-2", finding.GetProperty("externalId").GetString());
        Assert.Equal(1, finding.GetProperty("index").GetInt32());
        // `no-account-rule`, not `unknown-payee`: this host has never run the list sync, so `qb-lists.json` is empty
        // and the name check is skipped (T-902 treats a missing sync as a warning). The row is held either way —
        // which is the point: any hold at all refuses the batch.
        Assert.Equal("no-account-rule", finding.GetProperty("reason").GetString());
        Assert.Empty(body.GetProperty("unknownNames").GetProperty("vendors").EnumerateArray());
    }

    /// <summary>
    /// §6.2's <c>unknownNames</c>, with the lists actually synced: the name QuickBooks has never heard of is
    /// reported beside the row it would have been sent on, so the caller can add it once and re-send.
    /// </summary>
    [Fact]
    public async Task Should_NameTheVendorQuickBooksDoesNotHave_When_TheListsHaveBeenSynced()
    {
        new QbListsStore(_factory.Dir.Combine("data", "qb-lists.json")).Save(new QbLists
        {
            SyncedUtc = DateTime.UtcNow,
            Accounts = [new QbAccount { Name = "Chase Checking 4521" }, new QbAccount { Name = "Repairs and Maintenance" }],
            // The alias in rules.json turns "HOME DEPOT #6412" into "Home Depot", which is the name QuickBooks holds.
            Vendors = ["Home Depot"],
        });

        // The second row names its own expense account, so the mapper gets past tiers 1-2 and the list check is what
        // stops it. Without that it would hold on `no-account-rule` first, and a held row is never name-checked.
        var (status, body) = await PostAsync(SyncPostFixtures.HeldUnknownPayee);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Equal("unknown-payee", body.GetProperty("validation").EnumerateArray().Single().GetProperty("reason").GetString());
        Assert.Equal(
            "NEVER HEARD OF THEM",
            body.GetProperty("unknownNames").GetProperty("vendors").EnumerateArray().Single().GetString());
    }

    /// <summary>Every finding, not the first: a caller must be able to fix a refused batch in one pass (§6.2).</summary>
    [Fact]
    public async Task Should_ReportEveryFinding_When_SeveralRowsWouldBeHeld()
    {
        var (status, body) = await PostAsync(SyncPostFixtures.HeldSeveral);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(2, body.GetProperty("validation").GetArrayLength());
        Assert.Contains("nothing was sent to QuickBooks", body.GetProperty("detail").GetString());
    }

    // VP-1
    [Fact]
    public async Task Should_Answer400_When_TheRequestAsksForADryRun()
    {
        var (status, body) = await PostAsync(SyncPostFixtures.DryRunRefused);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("dry-run-not-supported", body.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    /// <summary>
    /// VP-1 again, for the value that looks harmless. <c>dryRun: false</c> is still the wrong route: a caller who
    /// sends it believes the field means something here, and the next thing they send might be <c>true</c>.
    /// </summary>
    [Fact]
    public async Task Should_Answer400_When_TheRequestSendsDryRunFalse()
    {
        var (status, _) = await PostAsync(SyncPostFixtures.DryRunFalseRefused);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer400_When_TheRequestAsksForModelAccounts()
    {
        var (status, body) = await PostAsync(SyncPostFixtures.ModelAccountsRefused);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("allowModelAccounts", body.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    // VP-4
    [Fact]
    public async Task Should_Answer400AndAlterNoAmount_When_TheControlTotalDoesNotMatch()
    {
        var (status, body) = await PostAsync(SyncPostFixtures.ControlTotalRefused);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("control-total-mismatch", body.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
        // A request that was never accepted gets no post id and no evidence folder.
        Assert.False(Directory.Exists(Path.Combine(BatchesRoot, "api-bad-total#1")));
    }

    /// <summary>
    /// VP-5. Busy means refused, not queued (§2.1): the caller is told at once, nothing is enqueued to surprise
    /// them later, and QuickBooks is not touched. The worker is held here by an unrelated piece of exclusive work,
    /// which is what a folder job looks like from this route.
    /// </summary>
    [Fact]
    public async Task Should_Answer409WithoutEnqueuing_When_TheWorkerIsBusy()
    {
        var queue = _factory.Services.GetRequiredService<JobQueue>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = queue.RunExclusiveAsync(
            "folder-job", async _ => { await release.Task; return 0; }, CancellationToken.None);

        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsync(Route, SyncPostFixtures.Content(SyncPostFixtures.PostedOneRow));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
        var problem = await response.ReadProblemAsync();
        Assert.Equal(5, problem.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Contains("Nothing was sent", problem.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
        // Nothing was queued behind the job either: releasing it must not post the batch a moment later.
        Assert.Equal(0, queue.Depth);
        release.SetResult();
        await busy;
        Assert.Empty(_factory.Gateway.Requests);
    }

    // VP-6
    [Fact]
    public async Task Should_Answer503AndPostNothing_When_TheBackupIsTooOld()
    {
        // FR-11 is not softened for this route: the guard runs on the worker, before anything is sent.
        var backups = _factory.Dir.Combine("backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "old.QBB"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(backups, "old.QBB"), DateTime.UtcNow.AddDays(-5));
        using var host = _factory.WithSetting("QuickBooks:BackupFolder", backups);

        var (status, _) = await PostAsync(SyncPostFixtures.PostedOneRow, host);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Empty(_factory.Gateway.Writes);
        Assert.Empty(new LedgerStore(_factory.LedgerFile).Load().Posted);
    }

    [Fact]
    public async Task Should_Answer503AndRecordNothing_When_QuickBooksIsUnavailable()
    {
        _factory.Gateway.Throw = new QuickBooksUnavailableException("simulated");

        var (status, body) = await PostAsync(SyncPostFixtures.PostedOneRow);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        // Nothing reached QuickBooks, so nothing is in the ledger and the request can simply be sent again.
        Assert.Empty(new LedgerStore(_factory.LedgerFile).Load().Posted);
        Assert.Equal("failed", body.GetProperty("status").GetString());
    }

    /// <summary>
    /// VP-7 (CLAUDE.md rule 7). The proof of ordering: QuickBooks throws, so nothing written after the send can
    /// have produced these files — yet the request is on disk with the caller's reference in it.
    /// </summary>
    [Fact]
    public async Task Should_WriteTheRequestBeforeAnythingIsSent_When_QuickBooksFails()
    {
        _factory.Gateway.Throw = new QuickBooksUnavailableException("simulated");

        var (_, body) = await PostAsync(SyncPostFixtures.PostedOneRow);

        var postId = body.GetProperty("postId").GetString()!;
        var request = Path.Combine(BatchesRoot, postId, ApiBatchStore.RequestFile);
        Assert.True(File.Exists(request));
        Assert.Contains("zemen-2019-12", File.ReadAllText(request));
    }

    // VP-8
    [Fact]
    public async Task Should_UndoExactlyThisPost_When_ThePostIdIsHandedToUndo()
    {
        var (_, body) = await PostAsync(SyncPostFixtures.PostedOneRow);
        var postId = body.GetProperty("postId").GetString()!;
        var ledger = new LedgerStore(_factory.LedgerFile).Load();
        Assert.Contains(ledger.Jobs, j => j.BatchId == postId);
        Assert.Contains(ledger.Posted, p => p.BatchId == postId && p.Amount == 184.32m);

        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsync("/api/v1/batches/" + Uri.EscapeDataString(postId) + "/undo", null);
        var undo = await response.Content.ReadFromJsonAsync<JsonElement>();

        // FR-13 works on a post that was never a pollable batch: the ledger, not a new store, is what records it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, undo.GetProperty("deleted").GetInt32());
        Assert.All(new LedgerStore(_factory.LedgerFile).Load().Posted, p => Assert.True(p.Undone));
    }

    /// <summary>
    /// VP-9. The dangerous case of §8: the post succeeds, the response is lost, the caller sends it again. With the
    /// key, the second request returns the <i>original</i> transaction ids and QuickBooks is written to once.
    /// </summary>
    [Fact]
    public async Task Should_PostOnceAndReturnTheSameTxnIds_When_TheSameKeyIsSentTwice()
    {
        var (first, firstBody) = await PostAsync(SyncPostFixtures.PostedOneRow, idempotencyKey: "zemen-2019-12-attempt-1");
        var (second, secondBody) = await PostAsync(SyncPostFixtures.PostedOneRow, idempotencyKey: "zemen-2019-12-attempt-1");

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Single(_factory.Gateway.Writes);
        Assert.Equal(
            firstBody.GetProperty("posted").EnumerateArray().Single().GetProperty("txnId").GetString(),
            secondBody.GetProperty("posted").EnumerateArray().Single().GetProperty("txnId").GetString());
        Assert.Equal(firstBody.GetProperty("postId").GetString(), secondBody.GetProperty("postId").GetString());
    }

    /// <summary>
    /// The trap this plan was written around. <c>/quickbooks/transactions/validate-and-post</c> is not
    /// <c>/quickbooks/transactions</c>, so it falls through to the <c>/quickbooks/</c> branch and would resolve to
    /// <c>qb:read</c> — the scope issued for reading lists and validating offline. A caller holding it must not be
    /// able to move money.
    /// </summary>
    [Fact]
    public async Task Should_Answer403AndPostNothing_When_TheKeyHoldsOnlyQbRead()
    {
        using var client = KeyedClient(ApiScopes.QbRead);

        using var response = await client.PostAsync(Route, SyncPostFixtures.Content(SyncPostFixtures.PostedOneRow));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains(ApiScopes.QbPost, problem.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer200_When_TheKeyHoldsQbPost()
    {
        using var client = KeyedClient(ApiScopes.QbPost);

        using var response = await client.PostAsync(Route, SyncPostFixtures.Content(SyncPostFixtures.PostedOneRow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// §6.1: there is no batch to poll here, so the body must not offer one. A <c>batchId</c> or a <c>dryRun</c> in
    /// this answer would invite a caller to poll a <c>GET</c> that will never accept this id, or to believe the
    /// route has a mode it does not have.
    /// </summary>
    [Fact]
    public async Task Should_OfferNoBatchToPoll_When_ThePostSucceeds()
    {
        var (_, body) = await PostAsync(SyncPostFixtures.PostedOneRow);

        Assert.False(body.TryGetProperty("batchId", out _));
        Assert.False(body.TryGetProperty("dryRun", out _));
        Assert.False(body.TryGetProperty("isRunning", out _));
        Assert.NotNull(body.GetProperty("postId").GetString());
    }

    /// <summary>
    /// §8: a retry <i>without</i> the key is a duplicate, and under §5.1 a duplicate row is a held row — so the
    /// answer is 422, not 200. The caller has to read it as "already posted", which is why the finding names the
    /// reason and the earlier post rather than only saying no.
    /// </summary>
    [Fact]
    public async Task Should_Answer422NamingTheEarlierPost_When_TheSameRowsAreSentAgainWithoutAKey()
    {
        var (_, first) = await PostAsync(SyncPostFixtures.PostedOneRow);
        var postId = first.GetProperty("postId").GetString();

        var (status, body) = await PostAsync(SyncPostFixtures.PostedOneRow);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Single(_factory.Gateway.Writes);
        var finding = body.GetProperty("validation").EnumerateArray().Single();
        Assert.Equal("already-posted", finding.GetProperty("reason").GetString());
        Assert.Equal(postId, finding.GetProperty("postedIn").GetString());
    }

    [Fact]
    public async Task Should_Answer400_When_ThereIsNoBody()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsync(
            Route, new StringContent("null", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
    }

    /// <summary>A client whose key carries exactly the scopes named, and nothing else.</summary>
    private HttpClient KeyedClient(params string[] scopes)
    {
        var file = _factory.Dir.Combine("data", "clients.json");
        var key = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        new ApiClientStore(file).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "acme-erp",
                    Name = "Acme ERP",
                    KeyHash = secret.KeyHash,
                    KeySalt = secret.KeySalt,
                    Scopes = scopes,
                    CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
