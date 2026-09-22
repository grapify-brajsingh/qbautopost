using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-908 / FR-A-8, FR-A-10, FR-A-11: <c>POST /api/v1/quickbooks/transactions</c> — the direct post. The same engine
/// as a folder job, entered from JSON instead of a statement.
/// <para>
/// Two properties carry the money here. <b>Nothing is sent before the request is on disk</b> (CLAUDE.md rule 7), so a
/// crash mid-post still says who asked for it; and <b>a dry run never writes to QuickBooks</b>.
/// </para>
/// </summary>
public sealed class DirectPostApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string BatchesRoot => _factory.Dir.Combine("data", "api-batches");

    private static object Row(
        string externalId = "row-1",
        decimal amount = 184.32m,
        string kind = "Check",
        string? payee = "HOME DEPOT #6412",
        string? memo = null,
        string date = "2026-08-05") =>
        new
        {
            externalId,
            kind,
            date,
            amount,
            account = "Chase Checking 4521",
            payee,
            memo = memo ?? "HOME DEPOT #6412 PURCHASE",
            last4 = "4521",
        };

    private static object Request(bool? dryRun, params object[] rows) => new
    {
        reference = "payroll-2026-09",
        dryRun,
        controlTotal = rows.Sum(r => (decimal)r.GetType().GetProperty("amount")!.GetValue(r)!),
        transactions = rows,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        object request, WebApplicationFactory<Program>? host = null)
    {
        using var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/transactions", request, ApiFactory.Json);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Should_NotWriteToQuickBooks_When_TheBatchIsADryRun()
    {
        // The default is Settings.DryRunDefault (true in this host): asking for a post without saying so posts nothing.
        var (status, body) = await PostAsync(Request(null, Row()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.Empty(_factory.Gateway.Writes);
        Assert.Empty(new LedgerStore(_factory.LedgerFile).Load().Posted);
    }

    [Fact]
    public async Task Should_PostAndReturnTheTxnIds_When_TheBatchIsNotADryRun()
    {
        var (status, body) = await PostAsync(Request(false, Row()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(184.32m, body.GetProperty("totals").GetProperty("posted").GetDecimal());
        var posted = body.GetProperty("posted").EnumerateArray().Single();
        Assert.StartsWith("FAKE-", posted.GetProperty("txnId").GetString());
        Assert.Equal("row-1", posted.GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task Should_NameTheBatchAfterTheReference_When_OneIsGiven()
    {
        var (_, body) = await PostAsync(Request(false, Row()));

        Assert.Equal("api-payroll-2026-09#1", body.GetProperty("batchId").GetString());
    }

    [Fact]
    public async Task Should_StartANewAttempt_When_TheSameReferenceIsPostedAgain()
    {
        await PostAsync(Request(false, Row()));

        // A different row, so G4 does not simply skip it — what matters is that the batch id is not reused.
        var (_, second) = await PostAsync(Request(false, Row(externalId: "row-2", amount: 99.10m, memo: "SHELL OIL 4491")));

        Assert.Equal("api-payroll-2026-09#2", second.GetProperty("batchId").GetString());
    }

    [Fact]
    public async Task Should_WriteTheRequestBeforeAnythingIsSent_When_QuickBooksFails()
    {
        // The proof of ordering: QuickBooks throws, so nothing after the send can have written these files.
        _factory.Gateway.Throw = new QuickBooksUnavailableException("simulated");

        var (status, body) = await PostAsync(Request(false, Row()));

        // §2: QuickBooks unreachable is a 503 — but the body is still the batch, so the caller can see its id.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        var batchId = body.GetProperty("batchId").GetString()!;
        Assert.True(File.Exists(Path.Combine(BatchesRoot, batchId, ApiBatchStore.RequestFile)));
        Assert.Contains("payroll-2026-09", File.ReadAllText(Path.Combine(BatchesRoot, batchId, ApiBatchStore.RequestFile)));
        // Nothing reached QuickBooks, so nothing is in the ledger and the batch can simply be re-sent.
        Assert.Empty(new LedgerStore(_factory.LedgerFile).Load().Posted);
        Assert.Equal("partial", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Should_KeepTheQbXmlAsEvidence_When_ABatchPosts()
    {
        var (_, body) = await PostAsync(Request(false, Row()));

        var folder = Path.Combine(BatchesRoot, body.GetProperty("batchId").GetString()!);
        Assert.True(File.Exists(Path.Combine(folder, ApiBatchStore.RequestQbXmlFile)));
        Assert.True(File.Exists(Path.Combine(folder, ApiBatchStore.ResponseQbXmlFile)));
        Assert.True(File.Exists(Path.Combine(folder, ApiBatchStore.ResultFile)));
    }

    [Fact]
    public async Task Should_RecordTheBatchInTheLedger_When_ItPosts()
    {
        var (_, body) = await PostAsync(Request(false, Row()));

        var ledger = new LedgerStore(_factory.LedgerFile).Load();
        var batchId = body.GetProperty("batchId").GetString();
        // The ledger is the one record of what posted — that is what makes G4 and undo work across both entrances.
        Assert.Contains(ledger.Jobs, j => j.BatchId == batchId);
        Assert.Contains(ledger.Posted, p => p.BatchId == batchId && p.Amount == 184.32m);
    }

    [Fact]
    public async Task Should_BePartial_When_OneRowIsHeld()
    {
        var unresolved = Row(externalId: "row-2", payee: "NEVER HEARD OF THEM", memo: "NEVER HEARD OF THEM LLC");

        var (status, body) = await PostAsync(Request(false, Row(), unresolved));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("partial", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("held").GetInt32());
        // The held row is reported with its reason, not silently dropped.
        Assert.Equal("row-2", body.GetProperty("held").EnumerateArray().Single().GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task Should_Answer400AndPersistNothing_When_TheControlTotalDoesNotMatch()
    {
        var request = new { reference = "bad-total", controlTotal = 1.00m, transactions = new[] { Row() }, dryRun = false };

        var (status, _) = await PostAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_factory.Gateway.Writes);
        // A request that was never accepted gets no batch id and no evidence folder.
        Assert.False(Directory.Exists(Path.Combine(BatchesRoot, "api-bad-total#1")));
    }

    [Fact]
    public async Task Should_ReturnTheSameBody_When_TheBatchIsFetchedAfterwards()
    {
        var (_, posted) = await PostAsync(Request(false, Row()));
        var batchId = posted.GetProperty("batchId").GetString()!;

        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.GetAsync("/api/v1/batches/" + Uri.EscapeDataString(batchId));
        var fetched = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(batchId, fetched.GetProperty("batchId").GetString());
        Assert.Equal("posted", fetched.GetProperty("status").GetString());
        Assert.Equal(
            posted.GetProperty("posted").EnumerateArray().Single().GetProperty("txnId").GetString(),
            fetched.GetProperty("posted").EnumerateArray().Single().GetProperty("txnId").GetString());
    }

    [Fact]
    public async Task Should_Answer404_When_TheBatchIsUnknown()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/batches/" + Uri.EscapeDataString("api-never-happened#1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_UndoADirectBatch_When_UndoIsAsked()
    {
        var (_, body) = await PostAsync(Request(false, Row()));
        var batchId = body.GetProperty("batchId").GetString()!;

        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsync("/api/v1/batches/" + Uri.EscapeDataString(batchId) + "/undo", null);
        var undo = await response.Content.ReadFromJsonAsync<JsonElement>();

        // FR-13 must work on a batch that never had a job folder — that is why the ledger, not a new store, records it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, undo.GetProperty("deleted").GetInt32());
        Assert.All(new LedgerStore(_factory.LedgerFile).Load().Posted, p => Assert.True(p.Undone));
    }

    [Fact]
    public async Task Should_Answer503AndPostNothing_When_TheBackupIsTooOld()
    {
        // FR-11: the guard runs before anything is sent, and it is not softened for the API.
        var backups = _factory.Dir.Combine("backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "old.QBB"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(backups, "old.QBB"), DateTime.UtcNow.AddDays(-5));
        using var host = _factory.WithSetting("QuickBooks:BackupFolder", backups);

        var (status, _) = await PostAsync(Request(false, Row()), host);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Empty(_factory.Gateway.Writes);
    }

    [Fact]
    public async Task Should_SkipTheRow_When_QuickBooksAlreadyHasIt()
    {
        // G4's QuickBooks half runs on this path too: the first post put the transaction in the simulated company,
        // and the ledger is cleared so only the QuickBooks-side check can catch the repeat.
        await PostAsync(Request(false, Row()));
        new LedgerStore(_factory.LedgerFile).Save(Ledger.Empty);

        var (_, second) = await PostAsync(Request(false, Row()));

        Assert.Equal(0, second.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(1, second.GetProperty("counts").GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Should_Answer202_When_ThePostOutlastsTheSynchronousBudget()
    {
        using var host = _factory.WithSetting("Api:SyncPostTimeoutSeconds", "1");
        _factory.Gateway.Hang = true;

        var (status, body) = await PostAsync(Request(false, Row()), host);

        Assert.Equal(HttpStatusCode.Accepted, status);
        var batchId = body.GetProperty("batchId").GetString()!;
        Assert.Equal("/api/v1/batches/" + batchId, body.GetProperty("location").GetString());
        // The caller stopped waiting; the work did not stop, so the batch still reports itself as running.
        Assert.Equal("posting", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Should_Answer202Immediately_When_TheCallerPrefersAsync()
    {
        using var client = _factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Add("Prefer", "respond-async");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/quickbooks/transactions", Request(false, Row()), ApiFactory.Json);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("api-payroll-2026-09#1", body.GetProperty("batchId").GetString());
    }
}
