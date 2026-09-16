using System.Net;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>ready → posting → posted|partial through <see cref="FakeQbGateway"/> (spec FR-10…FR-12).</summary>
public sealed class PostingApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public PostingApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Ledger LoadLedger() => new LedgerStore(_factory.LedgerFile).Load();

    private async Task<string> ReadyJob()
    {
        var folder = _factory.Dir.CopySampleJob();
        var view = await _client.RunToEndAsync(folder);
        Assert.Equal(JobStatus.Ready, view.Status);
        return folder;
    }

    private async Task<Endpoints.JobView> Post(string jobId = "2026-08-tropicana")
    {
        using var response = await _client.PostAsync($"/jobs/{jobId}/post", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await _client.WaitForJobAsync(jobId);
    }

    [Fact]
    public async Task Should_PostVerifiedLinesAndEndPartial_When_ReadyJobIsPosted()
    {
        var folder = await ReadyJob();

        var view = await Post();

        Assert.Equal(JobStatus.Partial, view.Status); // the two unknown-payee lines stay held
        Assert.Null(view.Error);
        Assert.Equal("2026-08-tropicana#1", view.BatchId);
        Assert.Equal(8, view.Counts.Posted);
        Assert.Equal(2, view.Counts.Held);
        Assert.Equal(view.Totals.ToPost, view.Totals.Posted);
        Assert.All(view.Posted, p => Assert.StartsWith("FAKE-", p.TxnId, StringComparison.Ordinal));
        Assert.Single(_factory.Gateway.Requests);
        Assert.True(File.Exists(Path.Combine(folder, "output", "response.qbxml")));
    }

    [Fact]
    public async Task Should_RecordBatchAndEntriesInLedger_When_Posted()
    {
        await ReadyJob();

        var view = await Post();

        var ledger = LoadLedger();
        var job = Assert.Single(ledger.Jobs);
        Assert.Equal("2026-08-tropicana#1", job.BatchId);
        Assert.Equal(8, job.Posted);
        Assert.Equal(2, job.Held);
        Assert.Equal(1, job.Skipped);
        Assert.Equal(8, ledger.Posted.Count);
        Assert.Equal(view.Posted.Select(p => p.TxnId).Order(), ledger.Posted.Select(p => p.TxnId).Order());
    }

    [Fact]
    public async Task Should_RefuseResubmission_When_JobWasPosted()
    {
        var folder = await ReadyJob();
        await Post();

        using var response = await _client.PostJobAsync(folder);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Should_SkipAlreadyPostedLinesAndUseNextBatch_When_ForcedRerunIsPosted()
    {
        var folder = await ReadyJob();
        await Post();

        using (var rerun = await _client.PostJobAsync(folder, force: true))
        {
            Assert.Equal(HttpStatusCode.Accepted, rerun.StatusCode);
        }

        var ready = await _client.WaitForJobAsync("2026-08-tropicana");
        Assert.Equal(0, ready.Counts.ToPost);
        Assert.Equal(9, ready.Counts.Skipped);

        var view = await Post();
        Assert.Equal("2026-08-tropicana#2", view.BatchId);
        Assert.Single(_factory.Gateway.Requests); // nothing left to send the second time
    }

    [Fact]
    public async Task Should_PostImmediately_When_DryRunIsFalse()
    {
        var folder = _factory.Dir.CopySampleJob();

        var view = await _client.RunToEndAsync(folder, dryRun: false);

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.False(view.DryRun);
        Assert.Equal(8, view.Counts.Posted);
        Assert.Single(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_HoldRefusedLineWithSdkMessage_When_QuickBooksRejectsIt()
    {
        await ReadyJob();
        _factory.Gateway.RejectLine = 2;

        var view = await Post();

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.Equal(7, view.Counts.Posted);
        Assert.Equal(3, view.Counts.Held);
        var refused = Assert.Single(view.Held, h => h.Reason == HoldReasons.QuickBooksRejected);
        Assert.Contains(FakeQbGateway.RejectStatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Note, StringComparison.Ordinal);
        Assert.Equal(7, LoadLedger().Posted.Count);
    }

    [Fact]
    public async Task Should_EndPartialWithoutLedgerRecord_When_QuickBooksIsUnavailable()
    {
        var folder = await ReadyJob();
        _factory.Gateway.Throw = new QuickBooksUnavailableException("no session");

        var view = await Post();

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.StartsWith("nothing posted", view.Error, StringComparison.Ordinal);
        Assert.Equal(0, view.Counts.Posted);
        Assert.Empty(LoadLedger().Jobs);
        using var rerun = await _client.PostJobAsync(folder);
        Assert.Equal(HttpStatusCode.Accepted, rerun.StatusCode);
    }

    [Fact]
    public async Task Should_RecordBatchAndAskForDuplicateCheck_When_QuickBooksCallFails()
    {
        var folder = await ReadyJob();
        _factory.Gateway.Throw = new InvalidOperationException("RPC server is unavailable");

        var view = await Post();

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.Contains("run duplicates before re-post", view.Error, StringComparison.Ordinal);
        Assert.Equal(10, view.Counts.Held);
        var job = Assert.Single(LoadLedger().Jobs);
        Assert.Equal(0, job.Posted);
        using var rerun = await _client.PostJobAsync(folder);
        Assert.Equal(HttpStatusCode.Conflict, rerun.StatusCode);
    }

    [Fact]
    public async Task Should_Return409_When_PostIsRequestedTwice()
    {
        await ReadyJob();
        _factory.Gateway.Hang = true;

        using var first = await _client.PostAsync("/jobs/2026-08-tropicana/post", null);
        using var second = await _client.PostAsync("/jobs/2026-08-tropicana/post", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
