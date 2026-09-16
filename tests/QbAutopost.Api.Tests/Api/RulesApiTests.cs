using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>FR-14 <c>POST /rules/alias</c> and <c>POST /rules/account</c> on a private copy of the sample rules.</summary>
public sealed class RulesApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;
    private readonly HttpClient _client;

    public RulesApiTests()
    {
        File.Copy(Fixtures.SampleRules, RulesFile);
        _host = _factory.WithSetting("Company:RulesFile", RulesFile);
        _client = _host.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _factory.Dispose();
    }

    private string RulesFile => _factory.Dir.Combine("rules.json");

    private string ListsFile => _factory.Dir.Combine("data", "qb-lists.json");

    private Rules Reload() => Rules.Load(RulesFile);

    private Task<HttpResponseMessage> PostAlias(string? fragment, string? name, string? kind) =>
        _client.PostAsJsonAsync("/rules/alias", new { fragment, name, kind });

    private Task<HttpResponseMessage> PostAccount(string? vendor, string? account) =>
        _client.PostAsJsonAsync("/rules/account", new { vendor, account });

    [Fact]
    public async Task Should_WriteVendorAlias_When_AliasIsPosted()
    {
        using var response = await PostAlias("UNKNOWN PLUMBER", "Unknown Plumber LLC", "vendor");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Unknown Plumber LLC", Reload().PayeeAliases["UNKNOWN PLUMBER"]);
    }

    [Fact]
    public async Task Should_WriteCustomerAlias_When_KindIsCustomer()
    {
        using var response = await PostAlias("sunrise property", "Sunrise Property Mgmt", "Customer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sunrise Property Mgmt", Reload().CustomerAliases["SUNRISE PROPERTY"]);
    }

    [Fact]
    public async Task Should_ReturnTheChange_When_RuleReplacesAnother()
    {
        using var response = await PostAccount("Amazon", "Supplies");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ("VendorAccounts", "Amazon", "Supplies", "Office Supplies"),
            (body.GetProperty("section").GetString(), body.GetProperty("key").GetString(),
             body.GetProperty("value").GetString(), body.GetProperty("previous").GetString()));
    }

    [Fact]
    public async Task Should_WriteVendorAccount_When_AccountIsPosted()
    {
        using var response = await PostAccount("Unknown Plumber LLC", "Repairs and Maintenance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Repairs and Maintenance", Reload().VendorAccounts["Unknown Plumber LLC"]);
    }

    [Theory]
    [InlineData("vendors")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Should_Return400_When_KindIsNotVendorOrCustomer(string? kind)
    {
        var before = File.ReadAllText(RulesFile);

        using var response = await PostAlias("ACME", "Acme Corp", kind);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("kind", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RulesFile));
    }

    [Fact]
    public async Task Should_Return400_When_FragmentIsBlank()
    {
        using var response = await PostAlias(" ", "Acme Corp", "vendor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Should_Return400AndKeepRules_When_AccountIsNotInSyncedLists()
    {
        new QbListsStore(ListsFile).Save(new QbLists
        {
            Accounts = [new QbAccount { Name = "Utilities", Type = "Expense" }],
            Vendors = ["Amazon"],
        });
        var before = File.ReadAllText(RulesFile);

        using var response = await PostAccount("Amazon", "Repairs and Maintenance");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("Repairs and Maintenance", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RulesFile));
    }

    [Fact]
    public async Task Should_AcceptAccount_When_SyncedListsContainIt()
    {
        new QbListsStore(ListsFile).Save(new QbLists
        {
            Accounts = [new QbAccount { Name = "Utilities", Type = "Expense" }],
            Vendors = ["Amazon"],
        });

        using var response = await PostAccount("Amazon", "Utilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return500_When_RulesFileIsMissing()
    {
        File.Delete(RulesFile);

        using var response = await PostAccount("Amazon", "Utilities");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.ReadProblemAsync();
        Assert.False(File.Exists(RulesFile));
    }

    [Fact]
    public async Task Should_Return401_When_ApiKeyIsMissing()
    {
        using var anonymous = _host.CreateClient();
        var before = File.ReadAllText(RulesFile);

        using var response = await anonymous.PostAsJsonAsync("/rules/alias", new { fragment = "ACME", name = "Acme", kind = "vendor" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, File.ReadAllText(RulesFile));
    }

    [Fact]
    public async Task Should_UseTaughtRulesInNextJob_When_RulesChangeBetweenJobs()
    {
        var folder = _factory.Dir.CopySampleJob();
        var first = await _client.RunToEndAsync(folder);
        Assert.Equal(JobStatus.Ready, first.Status);
        Assert.Equal(2, first.Counts.Held);

        (await PostAlias("UNKNOWN PLUMBER", "Unknown Plumber LLC", "vendor")).EnsureSuccessStatusCode();
        (await PostAccount("Unknown Plumber LLC", "Repairs and Maintenance")).EnsureSuccessStatusCode();
        (await PostAlias("SUNRISE PROPERTY", "Sunrise Property Mgmt", "customer")).EnsureSuccessStatusCode();
        using var post = await _client.PostAsync("/jobs/2026-08-tropicana/post", null);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var posted = await _client.WaitForJobAsync("2026-08-tropicana");

        Assert.Equal(JobStatus.Posted, posted.Status);
        Assert.Equal(0, posted.Counts.Held);
    }
}
