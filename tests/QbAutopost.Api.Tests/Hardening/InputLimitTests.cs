using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-911 / FR-A-17: input hardening. The two things a remote caller controls completely are how much they send and
/// what they put in a string, so both are bounded before anything expensive reads them.
/// <para>
/// The body cap matters more than it looks: T-909 buffers the whole body to hash it for the idempotency key, so
/// without a cap a caller can make the server hold in memory whatever it chooses to send.
/// </para>
/// </summary>
public sealed class InputLimitTests : IDisposable
{
    private const string PostRoute = "/api/v1/quickbooks/transactions";
    private const string JobsRoute = "/api/v1/jobs";

    private readonly ApiFactory _factory = new();

    [Fact]
    public async Task Should_Refuse_When_TheBodyIsLargerThanTheCap()
    {
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:MaxRequestBodyBytes"] = "1024",
        });
        using var client = Authorized(host);
        using var body = new StringContent(new string('x', 4096), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(PostRoute, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Should_Accept_When_TheBodyIsInsideTheCap()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(PostRoute, new { transactions = Array.Empty<object>() });

        // Refused on its contents (no rows), which is the point: the size gate let it through to be read.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("memo", 4097)]
    [InlineData("payee", 256)]
    [InlineData("account", 256)]
    [InlineData("lineAccount", 256)]
    public async Task Should_Refuse_When_AStringFieldIsTooLong(string field, int length)
    {
        using var client = _factory.CreateAuthorizedClient();
        var row = Row();
        row[field] = new string('x', length);

        using var response = await client.PostAsJsonAsync(
            PostRoute, new { controlTotal = 10.00m, transactions = new[] { row } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains(field, problem.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("memo", 4096)]
    [InlineData("payee", 255)]
    public async Task Should_NotComplainAboutLength_When_AStringFieldIsExactlyAtTheBound(string field, int length)
    {
        using var client = _factory.CreateAuthorizedClient();
        var row = Row();
        row[field] = new string('x', length);

        using var response = await client.PostAsJsonAsync(
            PostRoute, new { controlTotal = 10.00m, transactions = new[] { row } });

        // Whatever else happens to this row, it is not refused for its length.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await response.ReadProblemAsync();
            Assert.DoesNotContain("at most", problem.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Should_Refuse_When_TheJobFolderIsOutsideTheAllowList()
    {
        var allowed = _factory.Dir.Combine("allowed");
        Directory.CreateDirectory(allowed);
        var outside = _factory.Dir.CopySampleJob();
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["Paths:AllowedJobRoots:0"] = allowed,
        });
        using var client = Authorized(host);

        using var response = await client.PostAsJsonAsync(JobsRoute, new { folder = outside });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("AllowedJobRoots", problem.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_Accept_When_TheJobFolderIsInsideTheAllowList()
    {
        var root = _factory.Dir.Combine("allowed");
        Directory.CreateDirectory(root);
        var job = _factory.Dir.CopyJob(Fixtures.SampleJob, Path.Combine("allowed", "2026-08-tropicana"));
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["Paths:AllowedJobRoots:0"] = root,
        });
        using var client = Authorized(host);

        using var response = await client.PostAsJsonAsync(JobsRoute, new { folder = job });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Should_Refuse_When_TheJobFolderClimbsOutOfTheAllowList()
    {
        var root = _factory.Dir.Combine("allowed");
        Directory.CreateDirectory(root);
        _factory.Dir.CopySampleJob();
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["Paths:AllowedJobRoots:0"] = root,
        });
        using var client = Authorized(host);
        var traversal = Path.Combine(root, "..", "2026-08-tropicana");

        using var response = await client.PostAsJsonAsync(JobsRoute, new { folder = traversal });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_AcceptAnyAbsoluteFolder_When_NoAllowListIsConfigured()
    {
        // The shipped default: unrestricted on a loopback bind, and startup refuses a remote bind that leaves it so
        // (TransportTests.Should_Refuse_When_RemoteBindHasNoJobRootAllowList).
        using var client = _factory.CreateAuthorizedClient();
        var job = _factory.Dir.CopySampleJob();

        using var response = await client.PostAsJsonAsync(JobsRoute, new { folder = job });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private static Dictionary<string, object?> Row() => new()
    {
        ["kind"] = "Check",
        ["date"] = "2026-08-15",
        ["amount"] = 10.00m,
        ["account"] = "Operating",
        ["last4"] = "1234",
    };

    public void Dispose() => _factory.Dispose();
}
