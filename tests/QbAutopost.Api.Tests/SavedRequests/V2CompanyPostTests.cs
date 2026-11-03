using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1708 (M17 spec 4.7, A-7, A-12): a statement is validated and posted against its own company's file, rules and
/// ledger; a company that may not open a session, or whose file QuickBooks will not open, is refused as a problem.
/// </summary>
public sealed class V2CompanyPostTests : IDisposable
{
    private const string Route = "/api/v2/saved-requests";
    private const int TakomaId = 2;
    private const int SaId = 3;
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    // Companies:RootFolder must be absolute on the OS running the tests; on Windows these are the brief's C:\fake files.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\fake" : "/fake";
    private static readonly string TakomaFile = Path.Combine(Root, "Takoma.QBW");
    private static readonly string SaFile = Path.Combine(Root, "SA.QBW");

    private readonly ApiFactory _factory = new();
    private WebApplicationFactory<Program>? _host;

    public void Dispose()
    {
        _host?.Dispose();
        _factory.Dispose();
    }

    private FakeSavedRequestStore Store => _factory.Store;

    private FakeQbGateway Gateway => _factory.Gateway;

    /// <summary>The host with a company root; on first use it starts (bootstrap makes the default company 1) and seeds Takoma and S &amp; A.</summary>
    private WebApplicationFactory<Program> Host
    {
        get
        {
            if (_host is not null)
            {
                return _host;
            }

            _host = _factory.WithSetting("Companies:RootFolder", Root);
            _host.CreateClient().Dispose();
            return _host;
        }
    }

    private void SeedCompanies(
        CompanyAccess takomaAccess = CompanyAccess.Approved, string? takomaFile = null, string? takomaRules = null)
    {
        var host = Host;
        _factory.Companies.Add(CompanyOf(TakomaId, "Takoma Park Grocery Store Inc", takomaFile ?? TakomaFile, takomaAccess));
        _factory.Companies.Add(CompanyOf(SaId, "S & A Convenience Market LLC", SaFile, CompanyAccess.Approved));
        var folders = host.Services.GetRequiredService<CompanyDataFolder>();
        folders.Prepare(TakomaId);
        folders.Prepare(SaId);
        if (takomaRules is not null)
        {
            File.Copy(takomaRules, folders.RulesFileOf(TakomaId), overwrite: true);
        }
    }

    private static Company CompanyOf(int id, string name, string file, CompanyAccess access) =>
        new(id, null, null, name, file, [file], CompanyState.Active, access, T0, null, null, null, [], false, T0, T0, 1);

    private HttpClient Editor()
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Users.TokenFor(DashboardRoles.Editor));
        return client;
    }

    private static string Fixture(string folder, string name) => File.ReadAllText(Fixtures.PathOf(folder, name));

    private async Task<long> SaveAsync(string body, int? companyId)
    {
        using var client = Editor();
        using var response = await client.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
        if (companyId is { } company)
        {
            Store.SetCompany(id, company, CompanySource.Chosen);
        }

        return id;
    }

    private Task<long> SaveTakomaAsync() => SaveAsync(Fixture("saved-requests-v2", "post-false.json"), TakomaId);

    private async Task<(HttpStatusCode Status, JsonElement Body)> ActionAsync(long id, string action)
    {
        using var client = Editor();
        using var response = await client.PostAsync($"{Route}/{id}/{action}", null);
        return (response.StatusCode, await SavedRequestTestHost.BodyOf(response));
    }

    private async Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id) => await Store.AttemptsAsync(id, CancellationToken.None);

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    private static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    [Fact]
    public async Task Should_PostToTheStatementsCompanyFile_When_CompanyIsApproved()
    {
        SeedCompanies();
        var id = await SaveTakomaAsync();
        var defaultLedger = ReadOrNull(_factory.LedgerFile);

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        Assert.NotEmpty(Gateway.Targets);
        Assert.All(Gateway.Targets, t => Assert.Equal(TakomaFile, t));
        var ledgerFile = Host.Services.GetRequiredService<CompanyDataFolder>().LedgerFileOf(TakomaId);
        var postId = body.GetProperty("postId").GetString();
        Assert.Contains(new LedgerStore(ledgerFile).Load().Jobs, j => j.BatchId == postId);
        Assert.Equal(defaultLedger, ReadOrNull(_factory.LedgerFile));
    }

    [Fact]
    public async Task Should_Answer409OpenElsewhereAndNotRetry_When_AnotherCompanyFileIsOpen()
    {
        SeedCompanies();
        var id = await SaveTakomaAsync();
        Gateway.OpenCompanyFile = SaFile;

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-file-open-elsewhere", body.GetProperty("type").GetString());
        Assert.Equal(SaFile, body.GetProperty("openFile").GetString());
        Assert.Equal(TakomaFile, body.GetProperty("neededFile").GetString());
        Assert.Equal(AttemptOutcome.Unavailable, Assert.Single(await AttemptsAsync(id)).Outcome);
        Assert.Empty(Gateway.Writes);
        Assert.Equal(1, Gateway.Refusals);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_Answer503FileUnavailableAndNotRetry_When_CompanyFileCannotBeOpened()
    {
        SeedCompanies();
        var id = await SaveTakomaAsync();
        Gateway.UnavailableCompanyFile = TakomaFile;

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("company-file-unavailable", body.GetProperty("type").GetString());
        Assert.Equal(TakomaFile, body.GetProperty("neededFile").GetString());
        Assert.Equal(AttemptOutcome.Unavailable, Assert.Single(await AttemptsAsync(id)).Outcome);
        Assert.Empty(Gateway.Requests);
        Assert.Equal(1, Gateway.UnavailableCalls);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_Answer409NotReadyWithoutASession_When_CompanyNeedsApproval()
    {
        SeedCompanies(takomaAccess: CompanyAccess.NeedsApproval);
        var id = await SaveTakomaAsync();

        var (validateStatus, validate) = await ActionAsync(id, "validate");
        var (postStatus, post) = await ActionAsync(id, "post");

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (validateStatus, postStatus));
        foreach (var problem in new[] { validate, post })
        {
            Assert.Equal("company-not-ready", problem.GetProperty("type").GetString());
            Assert.Equal("active", problem.GetProperty("state").GetString());
            Assert.Equal("needs-approval", problem.GetProperty("access").GetString());
        }

        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer409NotReady_When_CompanyIsDisabledAfterValidation()
    {
        SeedCompanies();
        var id = await SaveTakomaAsync();
        var (validated, _) = await ActionAsync(id, "validate");
        Assert.Equal(HttpStatusCode.OK, validated);
        await _factory.Companies.UpdateAsync(TakomaId, 1, new CompanyPatch(null, null, true), "admin", T0, CancellationToken.None);

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-not-ready", body.GetProperty("type").GetString());
        Assert.Equal("disabled", body.GetProperty("state").GetString());
        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_RefuseSession_When_CompanyFileIsOutsideRootFolder()
    {
        var outside = OperatingSystem.IsWindows() ? @"C:\elsewhere\Takoma.QBW" : "/elsewhere/Takoma.QBW";
        SeedCompanies(takomaFile: outside);
        var id = await SaveTakomaAsync();

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-not-ready", body.GetProperty("type").GetString());
        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer409NotReady_When_StatementsCompanyNoLongerExists()
    {
        SeedCompanies();
        var id = await SaveAsync(Fixture("saved-requests-v2", "post-false.json"), 99);

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-not-ready", body.GetProperty("type").GetString());
        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_PlanWithTheCompanysRules_When_Validating()
    {
        SeedCompanies(takomaRules: Fixtures.PathOf("multi-company", "takoma-rules.json"));
        var statement = Fixture("multi-company", "takoma-statement.json");
        var takoma = await SaveAsync(statement, TakomaId);
        var onDefault = await SaveAsync(statement, null);

        var (takomaStatus, takomaBody) = await ActionAsync(takoma, "validate");
        var (defaultStatus, defaultBody) = await ActionAsync(onDefault, "validate");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (takomaStatus, defaultStatus));
        Assert.Equal("ok", takomaBody.GetProperty("validation").GetProperty("state").GetString());
        Assert.Equal("held", defaultBody.GetProperty("validation").GetProperty("state").GetString());
    }

    // TRANSITIONAL until T-1712: a statement with no company posts on the default company, as before M17.
    [Fact]
    public async Task Should_PostAsBefore_When_StatementHasNoCompanyYet()
    {
        SeedCompanies();
        var id = await SaveAsync(Fixture("saved-requests-v2", "post-false.json"), null);

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        Assert.NotEmpty(Gateway.Targets);
        Assert.All(Gateway.Targets, Assert.Null);
        Assert.Contains(new LedgerStore(_factory.LedgerFile).Load().Jobs, j => j.BatchId == body.GetProperty("postId").GetString());
    }
}
