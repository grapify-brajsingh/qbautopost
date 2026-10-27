using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Configuration;

/// <summary>
/// T-920: what a request does when <c>Company:RulesFile</c> names a file that is not there.
/// <para>
/// The app is <b>allowed</b> to run in this state and that is deliberate: <c>StartupLog</c> warns at startup and
/// <c>/health/ready</c> reports <c>rules: false</c>, so an operator can start the service before the file is in place
/// and watch readiness tell them. What it must not do is answer a request by throwing — an unhandled
/// <c>DirectoryNotFoundException</c> became a bare <c>500</c> with a stack trace in the log and no problem details,
/// which is neither the RFC 7807 contract (CLAUDE.md conventions) nor any use to the caller.
/// </para>
/// <para>
/// Found on a real machine: <c>appsettings.Development.json</c> names <c>../../samples/rules.json</c>, a path written
/// for the project folder, while the content root is <see cref="AppContext.BaseDirectory"/> (T-901, so a published
/// exe reads its own <c>appsettings.json</c>). The path therefore resolved inside <c>bin\</c> and nothing was there.
/// </para>
/// </summary>
public sealed class RulesFileMissingTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>A path that is certainly not there, under the factory's own temp folder so nothing is created.</summary>
    private string MissingRules => _factory.Dir.Combine("no-such-folder", "rules.json");

    private static object DirectRequest() => new
    {
        reference = "rules-missing",
        dryRun = true,
        controlTotal = 184.32m,
        transactions = new[]
        {
            new
            {
                externalId = "row-1",
                kind = "Check",
                date = "2026-08-05",
                amount = 184.32m,
                account = "Chase Checking 4521",
                payee = "HOME DEPOT #6412",
                memo = "HOME DEPOT #6412 PURCHASE",
                last4 = "4521",
            },
        },
    };

    private async Task<HttpResponseMessage> PostAsync(string route, object body)
    {
        var host = _factory.WithSetting("Company:RulesFile", MissingRules);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return await client.PostAsJsonAsync(route, body, ApiFactory.Json);
    }

    [Fact]
    public async Task Should_Answer503WithProblemDetails_When_TheRulesFileIsMissing()
    {
        using var response = await PostAsync("/api/v1/quickbooks/transactions/validate", DirectRequest());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The detail must name the path the server actually looked at. The whole defect was a path resolving somewhere
    /// nobody expected, so a message that does not say where is a message that would not have found it.
    /// </summary>
    [Fact]
    public async Task Should_NameTheResolvedPath_When_TheRulesFileIsMissing()
    {
        using var response = await PostAsync("/api/v1/quickbooks/transactions/validate", DirectRequest());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("rules", body.GetProperty("title").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MissingRules, body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The folder entrance loads the same file. Handling this at the endpoint would have fixed one route and left
    /// the others throwing, so it is handled where the file is read.
    /// </summary>
    [Fact]
    public async Task Should_Answer503_When_TheRulesFileIsMissingOnTheFolderPath()
    {
        using var response = await PostAsync("/api/v1/jobs/validate", new { folder = _factory.Dir.Path });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
