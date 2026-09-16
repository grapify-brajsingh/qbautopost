using System.Net;
using System.Net.Http.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>Dry-run lifecycle and error cases of the job routes (spec §6, plan T-107).</summary>
public sealed class JobsApiTests : IDisposable
{
    private const string UselessRequirement = "nothing useful here";

    /// <summary>What Hermes T1 answers for a requirement that names nothing: G2 then fails the job (no kinds).</summary>
    private const string EmptySpecJson = """{ "company": null, "kinds": [], "bankLast4": [], "cardLast4": [] }""";

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public JobsApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_AcceptAsQueued_When_SampleFolderIsSubmitted()
    {
        var folder = _factory.Dir.CopySampleJob();

        using var response = await _client.PostJobAsync(folder);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobEndpoints.CreateJobResponse>(ApiFactory.Json);
        Assert.Equal(new JobEndpoints.CreateJobResponse("2026-08-tropicana", JobStatus.Queued), body);
        Assert.Equal("/jobs/2026-08-tropicana", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Should_ReachReadyWithSpecJobView_When_SampleFolderIsDryRun()
    {
        var folder = _factory.Dir.CopySampleJob();

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.True(view.DryRun);
        Assert.Null(view.Error);
        Assert.Equal(ApiFactory.Company, view.Company);
        Assert.Equal(8, view.Counts.ToPost);
        Assert.Equal(2, view.Counts.Held);
        Assert.Equal(1, view.Counts.Skipped);
        Assert.Equal(0, view.Counts.Posted);
        Assert.Equal(2, view.Statements.Count);
        Assert.All(view.Statements, s => Assert.True(s.Reconcile!.Ok));
        Assert.All(view.Held, h => Assert.Equal(HoldReasons.UnknownPayee, h.Reason));
        Assert.Empty(view.Posted);
        Assert.Null(view.BatchId);
    }

    [Fact]
    public async Task Should_WriteOutputFiles_When_JobIsReady()
    {
        var folder = _factory.Dir.CopySampleJob();

        await _client.RunToEndAsync(folder);

        string[] files =
        [
            "status.json", "spec.json", "analysis.json", "result.json", "request.qbxml",
            "batch-enter-checks.csv", "batch-enter-creditcard.csv", "batch-enter-deposits.csv",
        ];
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(folder, "output", f)), f));
        Assert.False(File.Exists(Path.Combine(folder, "output", "response.qbxml")));
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_ReportUnsupportedFile_When_StatementsFolderHasOne()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.WriteAllText(Path.Combine(folder, "statements", "readme.txt"), "not a statement");

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        var unreadable = Assert.Single(view.Unreadable);
        Assert.Equal("statements/readme.txt", unreadable.File);
        Assert.Equal(HoldReasons.UnsupportedExtension, unreadable.Reason);
    }

    [Fact]
    public async Task Should_FailJobWithExplanation_When_RequirementStatesAccountWithoutStatement()
    {
        var folder = _factory.Dir.CopySampleJob();
        var requirement = Path.Combine(folder, "requirement.txt");
        File.WriteAllText(requirement, File.ReadAllText(requirement).Replace("(Last Four Digits) 4521", "(Last Four Digits) 4521 9999", StringComparison.Ordinal));
        _factory.Hermes.Respond(r => r.UserContent.Contains("4521 9999", StringComparison.Ordinal)
            ? FakeHermesClient.FixtureJson(HermesTask.Spec).Replace("\"bankLast4\": [\"4521\"]", "\"bankLast4\": [\"4521\", \"9999\"]", StringComparison.Ordinal)
            : null);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Contains("9999", view.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "output", "spec.json")));
    }

    [Fact]
    public async Task Should_FailJob_When_RulesFileIsBroken()
    {
        using var factory = new ApiFactory();
        using var client = factory.WithSetting("Company:RulesFile", factory.Dir.Combine("missing-rules.json")).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        var folder = factory.Dir.CopySampleJob();

        var view = await client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.StartsWith("analysis failed", view.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Return400_When_BodyIsEmpty()
    {
        using var response = await _client.PostAsync("/jobs", JsonContent.Create<object?>(null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Should_Return400WithErrors_When_RequirementIsMissing()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.Delete(Path.Combine(folder, "requirement.txt"));

        using var response = await _client.PostJobAsync(folder);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains(
            problem.GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.Contains("requirement.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_Return400_When_StatementsFolderIsEmpty()
    {
        var folder = _factory.Dir.CopySampleJob();
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "statements")))
        {
            File.Delete(file);
        }

        using var response = await _client.PostJobAsync(folder);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("relative/2026-08-tropicana")]
    [InlineData("")]
    public async Task Should_Return400_When_FolderIsNotAnAbsolutePath(string folder)
    {
        using var response = await _client.PostJobAsync(folder);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return400_When_FolderDoesNotExist()
    {
        using var response = await _client.PostJobAsync(_factory.Dir.Combine("no-such-job"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return404_When_JobIsUnknown()
    {
        using var get = await _client.GetAsync("/jobs/no-such-job");
        using var post = await _client.PostAsync("/jobs/no-such-job/post", null);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(404, (await get.ReadProblemAsync()).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Should_Return409_When_PostingAJobThatIsNotReady()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.WriteAllText(Path.Combine(folder, "requirement.txt"), UselessRequirement);
        _factory.Hermes.Respond(r => r.UserContent == UselessRequirement ? EmptySpecJson : null);
        var view = await _client.RunToEndAsync(folder);
        Assert.Equal(JobStatus.Failed, view.Status);

        using var response = await _client.PostAsync($"/jobs/{view.JobId}/post", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Return409_When_JobIsAlreadyInLedger()
    {
        new LedgerStore(_factory.LedgerFile).Save(new Ledger
        {
            Jobs = [new LedgerJob { JobId = "2026-08-tropicana", BatchId = "2026-08-tropicana#1" }],
        });
        var folder = _factory.Dir.CopySampleJob();

        using var response = await _client.PostJobAsync(folder);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("force", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Accept_When_JobIsInLedgerAndForced()
    {
        new LedgerStore(_factory.LedgerFile).Save(new Ledger
        {
            Jobs = [new LedgerJob { JobId = "2026-08-tropicana", BatchId = "2026-08-tropicana#1" }],
        });
        var folder = _factory.Dir.CopySampleJob();

        using var response = await _client.PostJobAsync(folder, force: true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JobStatus.Ready, (await _client.WaitForJobAsync("2026-08-tropicana")).Status);
    }

    [Fact]
    public async Task Should_Rerun_When_FinishedJobIsSubmittedAgain()
    {
        var folder = _factory.Dir.CopySampleJob();
        var first = await _client.RunToEndAsync(folder);

        var second = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, second.Status);
        Assert.True(second.UpdatedUtc >= first.UpdatedUtc);
    }

    [Fact]
    public async Task Should_ListJobsAndFilterByStatus_When_Asked()
    {
        var ready = _factory.Dir.CopySampleJob("2026-08-ready");
        var failed = _factory.Dir.CopySampleJob("2026-08-failed");
        File.WriteAllText(Path.Combine(failed, "requirement.txt"), UselessRequirement);
        _factory.Hermes.Respond(r => r.UserContent == UselessRequirement ? EmptySpecJson : null);
        await _client.RunToEndAsync(ready);
        await _client.RunToEndAsync(failed);

        var all = await _client.GetFromJsonAsync<List<JobSummary>>("/jobs", ApiFactory.Json);
        var onlyFailed = await _client.GetFromJsonAsync<List<JobSummary>>("/jobs?status=failed", ApiFactory.Json);

        Assert.Equal(["2026-08-failed", "2026-08-ready"], all!.Select(j => j.JobId).Order(StringComparer.Ordinal));
        Assert.Equal("2026-08-failed", Assert.Single(onlyFailed!).JobId);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("3")]
    public async Task Should_Return400_When_StatusFilterIsUnknown(string status)
    {
        using var response = await _client.GetAsync($"/jobs?status={status}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_FilterIgnoringCase_When_StatusIsUpperCase()
    {
        await _client.RunToEndAsync(_factory.Dir.CopySampleJob());

        var ready = await _client.GetFromJsonAsync<List<JobSummary>>("/jobs?status=READY", ApiFactory.Json);

        Assert.Equal("2026-08-tropicana", Assert.Single(ready!).JobId);
    }

    [Fact]
    public async Task Should_ReturnEmptyList_When_NoJobHasTheStatus()
    {
        await _client.RunToEndAsync(_factory.Dir.CopySampleJob());

        var posted = await _client.GetFromJsonAsync<List<JobSummary>>("/jobs?status=posted", ApiFactory.Json);

        Assert.Empty(posted!);
    }

    [Fact]
    public async Task Should_Return401_When_ListingWithoutApiKey()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.GetAsync("/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
