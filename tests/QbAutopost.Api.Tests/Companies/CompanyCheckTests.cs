using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Security;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Tests.Companies;

/// <summary>T-1710 (M17 spec 3, 5.1): <c>POST /companies/{id}/check</c> opens a session on the company file and stores access and names.</summary>
public sealed class CompanyCheckTests : IDisposable
{
    private const string Takoma = "18_Takoma Park Grocery Store Inc";
    private const string QbName = "Takoma Park Grocery Store Inc";

    private readonly ApiFactory _factory = new();
    private readonly FakeQbPresence _presence = new();
    private readonly string _root;
    private WebApplicationFactory<Program>? _host;
    private string _busyTimeout = "60";

    public CompanyCheckTests()
    {
        _root = _factory.Dir.Combine("companies-root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _host?.Dispose();
        _factory.Dispose();
    }

    private string? _defaultFile;

    private WebApplicationFactory<Program> Host => _host ??= _factory
        .WithSettings(new Dictionary<string, string?>
        {
            ["Companies:RootFolder"] = _root,
            ["QuickBooks:BusyTimeoutSeconds"] = _busyTimeout,
            ["Company:FilePath"] = _defaultFile ?? _factory.Dir.Combine("default", "Default.qbw"),
        })
        .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQbPresence>();
            services.AddSingleton<IQbPresence>(_presence);
        }));

    private HttpClient Client(string role = DashboardRoles.Admin)
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Users.TokenFor(role));
        return client;
    }

    private string Folder(string name, params string[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files.Length == 0 ? [name + ".qbw"] : files)
        {
            File.WriteAllText(Path.Combine(folder, file), "qbw");
        }

        return folder;
    }

    /// <summary>Scans a root holding one folder; returns the company's id, file and row version.</summary>
    private async Task<(int Id, string? File, int RowVersion)> ScannedAsync(string name = Takoma, params string[] files)
    {
        Folder(name, files);
        using var client = Client();
        using var response = await client.PostAsync("/api/v2/companies/scan", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("folderName").GetString() == name);
        var path = item.GetProperty("filePath");
        return (item.GetProperty("id").GetInt32(), path.ValueKind == JsonValueKind.String ? path.GetString() : null, item.GetProperty("rowVersion").GetInt32());
    }

    private async Task<HttpResponseMessage> CheckAsync(int id, string role = DashboardRoles.Admin)
    {
        using var client = Client(role);
        return await client.PostAsync($"/api/v2/companies/{id}/check", null);
    }

    private async Task<Company> StoredAsync(int id) => (await _factory.Companies.GetAsync(id, default))!;

    [Fact]
    public async Task Should_RecordApprovedAndNames_When_SessionOpens()
    {
        var (id, file, _) = await ScannedAsync();
        _factory.Gateway.CompanyFor(file!).CompanyName = QbName;

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", body.GetProperty("access").GetString());
        Assert.Equal(QbName, body.GetProperty("qbCompanyName").GetString());
        Assert.NotNull(body.GetProperty("accessCheckedUtc").GetString());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal([file], _factory.Gateway.Targets);
    }

    [Fact]
    public async Task Should_RecordCompanyCheckedEvent_When_CheckRuns()
    {
        var (id, _, _) = await ScannedAsync();

        using var response = await CheckAsync(id);

        Assert.Contains(_factory.Companies.AuditEvents, e => e.Kind == AuditActions.CompanyChecked && e.CompanyId == id);
    }

    [Fact]
    public async Task Should_Answer409OpenElsewhereAndKeepAccess_When_AnotherFileIsOpen()
    {
        var (id, file, _) = await ScannedAsync();
        _factory.Gateway.OpenCompanyFile = @"C:\other\Open.QBW";

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("company-file-open-elsewhere", problem.GetProperty("type").GetString());
        Assert.Equal(@"C:\other\Open.QBW", problem.GetProperty("openFile").GetString());
        Assert.Equal(file, problem.GetProperty("neededFile").GetString());
        Assert.Equal(CompanyAccess.Unchecked, (await StoredAsync(id)).Access);
    }

    [Fact]
    public async Task Should_Answer503FileUnavailableAndKeepAccess_When_SessionCannotOpenForSomeOtherReason()
    {
        var (id, file, _) = await ScannedAsync();
        _factory.Gateway.UnavailableCompanyFile = file;

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("company-file-unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.Equal(CompanyAccess.Unchecked, (await StoredAsync(id)).Access);
        Assert.Equal(1, _factory.Gateway.UnavailableCalls); // never retried
    }

    [Fact]
    public async Task Should_RecordError_When_SessionCannotOpen()
    {
        var (id, _, _) = await ScannedAsync();
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("simulated: no session");

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", body.GetProperty("access").GetString());
        Assert.Contains("no session", body.GetProperty("accessMessage").GetString());
    }

    [Fact]
    public async Task Should_RecordNeedsApproval_When_CallOutlivesTheBusyTimeout()
    {
        _busyTimeout = "1";
        var (id, _, _) = await ScannedAsync();
        _factory.Gateway.QueryHang = true;

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("needs-approval", body.GetProperty("access").GetString());
        Assert.Contains("certificate dialog", body.GetProperty("accessMessage").GetString());
    }

    // ---- final review (3): a failed check never downgrades the default company ------------------------------------

    /// <summary>The default company of an upgraded install, approved at bootstrap, with its configured file on disk.</summary>
    private async Task<Company> DefaultCompanyAsync()
    {
        _defaultFile = _factory.Dir.Combine("default", "Default.qbw");
        Directory.CreateDirectory(Path.GetDirectoryName(_defaultFile)!);
        File.WriteAllText(_defaultFile, "qbw");
        _ = Host.Server; // starts the host, which creates the default company
        var company = (await _factory.Companies.ListAsync(default)).Single(c => c.IsDefault);
        Assert.Equal(CompanyAccess.Approved, company.Access);
        return company;
    }

    [Fact]
    public async Task Should_Answer503FileUnavailableAndKeepApproval_When_TheDefaultCompanysSessionCannotOpen()
    {
        var company = await DefaultCompanyAsync();
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("simulated: no session");

        using var response = await CheckAsync(company.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("company-file-unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.Equal(company, await StoredAsync(company.Id));
        Assert.DoesNotContain(_factory.Companies.AuditEvents, e => e.Kind == AuditActions.CompanyChecked);
    }

    [Fact]
    public async Task Should_AnswerNeedsApprovalWithoutStamping_When_TheDefaultCompanysCallOutlivesTheBusyTimeout()
    {
        _busyTimeout = "1";
        var company = await DefaultCompanyAsync();
        _factory.Gateway.QueryHang = true;

        using var response = await CheckAsync(company.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("needs-approval", body.GetProperty("access").GetString());
        Assert.Contains("certificate dialog", body.GetProperty("accessMessage").GetString());
        Assert.Equal(company, await StoredAsync(company.Id));
        Assert.DoesNotContain(_factory.Companies.AuditEvents, e => e.Kind == AuditActions.CompanyChecked);
    }

    [Fact]
    public async Task Should_Answer503NotOpenWithoutASession_When_QuickBooksIsNotRunning()
    {
        var (id, _, _) = await ScannedAsync();
        _presence.IsOpen = false;

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("quickbooks-not-open", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Equal(CompanyAccess.Unchecked, (await StoredAsync(id)).Access);
    }

    [Fact]
    public async Task Should_RecordFileNotFoundWithoutASession_When_QbwIsGone()
    {
        var (id, file, _) = await ScannedAsync();
        File.Delete(file!);

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("file-not-found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_CheckAnUnapprovedCompany_When_AdminAsks()
    {
        var (id, file, _) = await ScannedAsync();
        await _factory.Companies.RecordAccessAsync(
            id, new CompanyAccessCheck(CompanyAccess.NeedsApproval, "dialog", null, null, DateTime.UtcNow, file), "admin", default);

        using var response = await CheckAsync(id);

        Assert.Equal("approved", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access").GetString());
        Assert.Single(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer409NotReadyWithoutASession_When_CompanyIsDisabled()
    {
        var (id, _, rowVersion) = await ScannedAsync();
        using (var client = Client())
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v2/companies/{id}") { Content = JsonContent.Create(new { disabled = true }) })
        {
            patch.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
            using var disabled = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        }

        using var response = await CheckAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("company-not-ready", problem.GetProperty("type").GetString());
        Assert.Equal("disabled", problem.GetProperty("state").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer409NotReadyWithoutASession_When_AmbiguousWithNoChosenFile()
    {
        var (id, file, _) = await ScannedAsync("440_Two Files", "a.qbw", "b.qbw");

        using var response = await CheckAsync(id);

        Assert.Null(file);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ambiguous", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer409NotReadyWithoutASession_When_FileIsOutsideTheRoot()
    {
        var outside = Path.Combine(_factory.Dir.Combine("elsewhere"), "Out.qbw");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "qbw");
        _ = Host.Server; // starts the host, which creates the default company first
        var company = _factory.Companies.Add(new Company(
            50, "Out", "50", "Out Inc", outside, [outside], CompanyState.Active, CompanyAccess.Unchecked, DateTime.UtcNow, null,
            null, null, [], false, DateTime.UtcNow, DateTime.UtcNow, 1));

        using var response = await CheckAsync(company.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("company-not-ready", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_StampNothing_When_CompanyIsRepointedWhileQuickBooksAnswers()
    {
        var (id, file, rowVersion) = await ScannedAsync("440_Two Files", "a.qbw", "b.qbw");
        var a = Path.Combine(_root, "440_Two Files", "a.qbw");
        var b = Path.Combine(_root, "440_Two Files", "b.qbw");
        var chosen = await _factory.Companies.UpdateAsync(id, rowVersion, new CompanyPatch(null, a, null), "admin", DateTime.UtcNow, default);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Gateway.QueryGate = gate.Task;

        var check = CheckAsync(id);
        await WaitUntilAsync(() => _factory.Gateway.Requests.Count == 1);
        await _factory.Companies.UpdateAsync(id, chosen.Company!.RowVersion, new CompanyPatch(null, b, null), "admin", DateTime.UtcNow, default);
        gate.SetResult();
        using var response = await check;

        Assert.Null(file);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var stored = await StoredAsync(id);
        Assert.Equal((b, CompanyAccess.Unchecked, null), (stored.FilePath, stored.Access, stored.QbCompanyName));
    }

    [Fact]
    public async Task Should_Answer404_When_CompanyIsUnknown()
    {
        using var response = await CheckAsync(999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(DashboardRoles.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(DashboardRoles.Editor, HttpStatusCode.Forbidden)]
    [InlineData(DashboardRoles.Admin, HttpStatusCode.OK)]
    public async Task Should_AllowOnlyAdmin_When_CheckRequested(string role, HttpStatusCode expected)
    {
        var (id, _, _) = await ScannedAsync();

        using var response = await CheckAsync(id, role);

        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition did not become true in time");
    }
}
