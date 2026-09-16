using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>FR-11 around the add request: posting transition, retry once, backup-age guard, response.qbxml.</summary>
public sealed class PostingSafetyApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private WebApplicationFactory<Program> _host;
    private HttpClient _client;

    public PostingSafetyApiTests()
    {
        _host = _factory;
        _client = _factory.CreateAuthorizedClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private string Folder { get; set; } = "";

    private string BackupDir => _factory.Dir.Combine("backups");

    private void UseBackupFolder()
    {
        Directory.CreateDirectory(BackupDir);
        _client.Dispose();
        _host = _factory.WithSetting("QuickBooks:BackupFolder", BackupDir);
        _client = _host.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
    }

    private void Backup(double hoursOld)
    {
        var path = Path.Combine(BackupDir, "Tropicana.QBB");
        File.WriteAllText(path, "qbb");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hoursOld));
    }

    private async Task Ready()
    {
        Folder = _factory.Dir.CopySampleJob();
        Assert.Equal(JobStatus.Ready, (await _client.RunToEndAsync(Folder)).Status);
    }

    private async Task<Endpoints.JobView> Post()
    {
        using var response = await _client.PostAsync("/jobs/2026-08-tropicana/post", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await _client.WaitForJobAsync("2026-08-tropicana");
    }

    private Ledger Ledger => new LedgerStore(_factory.LedgerFile).Load();

    [Fact]
    public async Task Should_PersistPostingBeforeCallingQuickBooks_When_PostIsAccepted()
    {
        await Ready();
        _factory.Gateway.Hang = true;

        using (await _client.PostAsync("/jobs/2026-08-tropicana/post", null))
        {
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_factory.Gateway.Writes.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Single(_factory.Gateway.Writes);
        Assert.Equal(JobStatus.Posting, (await _client.GetJobAsync("2026-08-tropicana")).Status);
        Assert.Contains("\"posting\"", File.ReadAllText(JobRecord.StatusFileOf(Folder)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_WriteQuickBooksAnswerToResponseFile_When_Posted()
    {
        await Ready();

        var view = await Post();

        var response = File.ReadAllText(Path.Combine(Folder, "output", "response.qbxml"));
        Assert.All(view.Posted, p => Assert.Contains($"<TxnID>{p.TxnId}</TxnID>", response, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_RetryOnceAndPost_When_SessionFailsToOpenTheFirstTime()
    {
        await Ready();
        _factory.Gateway.FailWritesTimes = 1;

        var view = await Post();

        Assert.Equal(8, view.Counts.Posted);
        Assert.Equal(2, _factory.Gateway.Writes.Count);
        Assert.Equal(8, Ledger.Posted.Count);
    }

    [Fact]
    public async Task Should_PostNothing_When_SessionFailsToOpenTwice()
    {
        await Ready();
        _factory.Gateway.FailWritesTimes = 2;

        var view = await Post();

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.StartsWith("nothing posted: QuickBooks unavailable", view.Error, StringComparison.Ordinal);
        Assert.Equal(2, _factory.Gateway.Writes.Count);
        Assert.Empty(Ledger.Jobs);
    }

    [Fact]
    public async Task Should_NotResendAdds_When_ComErrorHitsTheAddRequest()
    {
        await Ready();
        _factory.Gateway.Throw = new QuickBooksCallException("ProcessRequest failed (0x80010105)", unchecked((int)0x80010105));

        var view = await Post();

        Assert.Single(_factory.Gateway.Writes);
        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.Contains("run duplicates before re-post", view.Error, StringComparison.Ordinal);
        Assert.Equal(8, view.Held.Count(h => h.Reason == HoldReasons.QuickBooksNoResponse));
        Assert.Single(Ledger.Jobs);
    }

    [Fact]
    public async Task Should_FailBackupTooOldAndSendNothing_When_NewestBackupIsTooOld()
    {
        UseBackupFolder();
        Backup(hoursOld: 40);
        await Ready();

        var view = await Post();

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.StartsWith(HoldReasons.BackupTooOld, view.Error, StringComparison.Ordinal);
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Empty(Ledger.Jobs);
        Assert.Contains("\"failed\"", File.ReadAllText(Path.Combine(Folder, "output", "result.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_AllowResubmission_When_JobFailedForBackupAge()
    {
        UseBackupFolder();
        Backup(hoursOld: 40);
        await Ready();
        await Post();
        Backup(hoursOld: 1);

        using var rerun = await _client.PostJobAsync(Folder);

        Assert.Equal(HttpStatusCode.Accepted, rerun.StatusCode);
    }

    [Fact]
    public async Task Should_Post_When_BackupIsRecent()
    {
        UseBackupFolder();
        Backup(hoursOld: 2);
        await Ready();

        var view = await Post();

        Assert.Equal(8, view.Counts.Posted);
    }

    [Fact]
    public async Task Should_FailBackupTooOld_When_DryRunIsFalse()
    {
        UseBackupFolder();
        Folder = _factory.Dir.CopySampleJob();

        var view = await _client.RunToEndAsync(Folder, dryRun: false);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.StartsWith("backup-too-old: no .QBB backup", view.Error, StringComparison.Ordinal);
        Assert.Empty(_factory.Gateway.Requests);
    }
}
