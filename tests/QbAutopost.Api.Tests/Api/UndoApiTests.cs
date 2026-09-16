using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>FR-13 <c>POST /batches/{id}/undo</c> through the host and the fake company.</summary>
public sealed class UndoApiTests : IDisposable
{
    private const string UndoUrl = "/batches/2026-08-tropicana%231/undo";

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _folder = "";

    public UndoApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Ledger Ledger => new LedgerStore(_factory.LedgerFile).Load();

    private async Task Posted()
    {
        _folder = _factory.Dir.CopySampleJob();
        Assert.Equal(JobStatus.Ready, (await _client.RunToEndAsync(_folder)).Status);
        using (var post = await _client.PostAsync("/jobs/2026-08-tropicana/post", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        }

        Assert.Equal(JobStatus.Partial, (await _client.WaitForJobAsync("2026-08-tropicana")).Status);
        Assert.Equal(8, _factory.Gateway.Company.Transactions.Count);
    }

    private async Task<JsonElement> UndoOk()
    {
        using var response = await _client.PostAsync(UndoUrl, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Should_DeleteEveryPostedTransaction_When_BatchIsUndone()
    {
        await Posted();

        var body = await UndoOk();

        Assert.Equal(8, body.GetProperty("deleted").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetArrayLength());
        Assert.Empty(_factory.Gateway.Company.Transactions);
    }

    [Fact]
    public async Task Should_MarkLedgerAndJobUndone_When_AllDeletesSucceed()
    {
        await Posted();

        await UndoOk();

        Assert.All(Ledger.Posted, p => Assert.True(p.Undone));
        Assert.True(Assert.Single(Ledger.Jobs).Undone);
        Assert.Equal(JobStatus.Undone, (await _client.GetJobAsync("2026-08-tropicana")).Status);
        Assert.Contains("\"undone\"", File.ReadAllText(JobRecord.StatusFileOf(_folder)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_WriteUndoAuditFilesToJobOutput_When_Undoing()
    {
        await Posted();

        await UndoOk();

        Assert.True(File.Exists(Path.Combine(_folder, "output", "undo-2026-08-tropicana-1.request.qbxml")));
        Assert.True(File.Exists(Path.Combine(_folder, "output", "undo-2026-08-tropicana-1.response.qbxml")));
    }

    [Fact]
    public async Task Should_ReportFailedDeleteAndKeepJobPartial_When_TransactionIsAlreadyGone()
    {
        await Posted();
        var gone = Ledger.Posted[0];
        _factory.Gateway.Company.Process(QbTxnDelete.Build([new TxnToDelete(gone.Kind, gone.TxnId)]));

        var body = await UndoOk();

        Assert.Equal(7, body.GetProperty("deleted").GetInt32());
        var failed = Assert.Single(body.GetProperty("failed").EnumerateArray().ToList());
        Assert.Equal(gone.TxnId, failed.GetProperty("txnId").GetString());
        Assert.StartsWith("3120: ", failed.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(JobStatus.Partial, (await _client.GetJobAsync("2026-08-tropicana")).Status);
        Assert.False(Ledger.Jobs[0].Undone);
    }

    [Fact]
    public async Task Should_Return404_When_BatchIsUnknown()
    {
        using var response = await _client.PostAsync("/batches/nope%231/undo", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.ReadProblemAsync();
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Return503AndMarkNothing_When_QuickBooksIsUnavailable()
    {
        await Posted();
        _factory.Gateway.Throw = new QuickBooksUnavailableException("no session");

        using var response = await _client.PostAsync(UndoUrl, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.All(Ledger.Posted, p => Assert.False(p.Undone));
        Assert.Equal(8, _factory.Gateway.Company.Transactions.Count);
    }

    [Fact]
    public async Task Should_Return401_When_ApiKeyIsMissing()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.PostAsync(UndoUrl, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_PostTheLinesAgain_When_ForcedRerunFollowsUndo()
    {
        await Posted();
        await UndoOk();

        using (var rerun = await _client.PostJobAsync(_folder, force: true))
        {
            Assert.Equal(HttpStatusCode.Accepted, rerun.StatusCode);
        }

        var ready = await _client.WaitForJobAsync("2026-08-tropicana");
        Assert.Equal((8, 1), (ready.Counts.ToPost, ready.Counts.Skipped));
        Assert.DoesNotContain(ready.Skipped, s => s.Reason == HoldReasons.AlreadyPosted);
    }

    [Fact]
    public async Task Should_ListJobUnderUndone_When_BatchIsUndone()
    {
        await Posted();
        await UndoOk();

        var undone = await _client.GetFromJsonAsync<List<Endpoints.JobSummary>>("/jobs?status=undone", ApiFactory.Json);
        var partial = await _client.GetFromJsonAsync<List<Endpoints.JobSummary>>("/jobs?status=partial", ApiFactory.Json);

        var job = Assert.Single(undone!);
        Assert.Equal(("2026-08-tropicana", "2026-08-tropicana#1"), (job.JobId, job.BatchId));
        Assert.Empty(partial!);
    }
}
