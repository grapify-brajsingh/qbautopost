using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-914 (api-v1 FR-A-7): <c>POST /api/v1/jobs/validate</c> — "would this folder run?" answered without queueing a
/// job, without writing into the folder and without touching QuickBooks.
/// <para>
/// It exists so an operator (or an integration dropping folders on a share) can find a missing
/// <c>requirement.txt</c> or a statement that will not reconcile before a job id is burned and before the ledger
/// has an opinion. 200 when it would run, 422 with the same body when it would not — a validator that answers only
/// on success tells the caller nothing (FR-A-6's rule, applied here too).
/// </para>
/// </summary>
public sealed class FolderValidateApiTests : IDisposable
{
    private const string Route = ApiRoutes.V1Prefix + "/jobs/validate";

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;
    private WebApplicationFactory<Program>? _host;

    public FolderValidateApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _host?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_Return200WithEveryStatement_When_TheFolderWouldRun()
    {
        var folder = _factory.Dir.CopySampleJob();

        using var response = await _client.PostAsJsonAsync(Route, new { folder });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(2, body.GetProperty("statements").GetArrayLength());
        Assert.All(
            body.GetProperty("statements").EnumerateArray(),
            s => Assert.True(s.GetProperty("reconcile").GetProperty("ok").GetBoolean(), s.GetProperty("file").GetString()));
    }

    [Fact]
    public async Task Should_Return422WithTheReasons_When_TheFolderIsNotAJobFolder()
    {
        var folder = _factory.Dir.Combine("empty-folder");
        Directory.CreateDirectory(folder);

        using var response = await _client.PostAsJsonAsync(Route, new { folder });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "requirement.txt is missing",
            body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>FR-A-7: no job is queued and nothing is written into the folder, not even <c>output/</c>.</summary>
    [Fact]
    public async Task Should_QueueNothingAndWriteNothing_When_AFolderIsValidated()
    {
        var folder = _factory.Dir.CopySampleJob();

        using var response = await _client.PostAsJsonAsync(Route, new { folder });

        response.EnsureSuccessStatusCode();
        Assert.False(Directory.Exists(Path.Combine(folder, FolderReader.OutputDirName)));
        Assert.False(File.Exists(_factory.JobIndexFile));
        using var jobs = await _client.GetAsync(ApiRoutes.V1Prefix + "/jobs");
        Assert.Empty((await jobs.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Should_NeverCallQuickBooks_When_AFolderIsValidated()
    {
        var folder = _factory.Dir.CopySampleJob();

        using var response = await _client.PostAsJsonAsync(Route, new { folder });

        response.EnsureSuccessStatusCode();
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Return400_When_NoFolderIsGiven()
    {
        using var response = await _client.PostAsJsonAsync(Route, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("A folder is required", problem.GetProperty("title").GetString());
    }

    /// <summary>
    /// T-911 (FR-A-17): the folder is a stranger's string on this route as much as on <c>POST /jobs</c>, so the
    /// same allow-list decides. A validate that read any path on the server would be a directory probe with a
    /// friendly name.
    /// </summary>
    [Fact]
    public async Task Should_Return400_When_TheFolderIsOutsideTheAllowedJobRoots()
    {
        var allowed = _factory.Dir.Combine("allowed");
        Directory.CreateDirectory(allowed);
        var folder = _factory.Dir.CopySampleJob();
        _host = _factory.WithSetting("Paths:AllowedJobRoots:0", allowed);
        using var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.PostAsJsonAsync(Route, new { folder });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return401_When_NoKeyIsSent()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(Route, new { folder = _factory.Dir.CopySampleJob() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// api-v1 §9 and handoff trap 19: the flat path answers too while <c>Api:LegacyRoutes</c> is true, and the
    /// route is listed in <see cref="ApiRoutes"/> as a POST that changes nothing, so it leaves no audit line.
    /// </summary>
    [Fact]
    public async Task Should_AnswerTheFlatPathToo_When_LegacyRoutesAreOn()
    {
        using var response = await _client.PostAsJsonAsync("/jobs/validate", new { folder = _factory.Dir.CopySampleJob() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
