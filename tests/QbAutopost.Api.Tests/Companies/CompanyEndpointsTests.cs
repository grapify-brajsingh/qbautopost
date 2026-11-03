using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.SavedRequests;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Security;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Tests.Companies;

/// <summary>T-1705 (M17 spec 4.2, 5.1): <c>GET /companies</c>, <c>POST /companies/scan</c>, <c>PATCH /companies/{id}</c>.</summary>
public sealed class CompanyEndpointsTests : IDisposable
{
    private const string Alpha = "18_Takoma Park Grocery Store Inc";
    private const string Beta = "440_S & A Convenience Market LLC";
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory = new();
    private readonly string _root;
    private WebApplicationFactory<Program>? _host;

    public CompanyEndpointsTests()
    {
        _root = _factory.Dir.Combine("companies-root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _host?.Dispose();
        _factory.Dispose();
    }

    private WebApplicationFactory<Program> Host =>
        _host ??= _factory.WithSetting("Companies:RootFolder", _root);

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

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> ScanAsync()
    {
        using var client = Client();
        using var response = await client.PostAsync("/api/v2/companies/scan", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private async Task<JsonElement> ListAsync(string role = DashboardRoles.Admin)
    {
        using var client = Client(role);
        return await BodyAsync(await client.GetAsync("/api/v2/companies"));
    }

    private static JsonElement Item(JsonElement body, string folderName) =>
        body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("folderName").GetString() == folderName);

    private async Task<HttpResponseMessage> PatchAsync(
        int id, int? rowVersion, object body, string role = DashboardRoles.Admin)
    {
        using var client = Client(role);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v2/companies/{id}") { Content = JsonContent.Create(body) };
        if (rowVersion is { } version)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return await client.SendAsync(request);
    }

    /// <summary>Scans a root holding one company folder with the given files; returns its id and row version.</summary>
    private async Task<(int Id, int RowVersion)> ScannedAsync(string name, params string[] files)
    {
        Folder(name, files);
        var company = Item(await ScanAsync(), name);
        return (company.GetProperty("id").GetInt32(), company.GetProperty("rowVersion").GetInt32());
    }

    // ---- scan --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ReturnTwoActiveCompaniesWithCodes_When_RootHoldsTwoCompanyFolders()
    {
        Folder(Alpha);
        Folder(Beta);

        var body = await ScanAsync();

        Assert.Equal(2, body.GetProperty("added").GetInt32());
        var added = body.GetProperty("items").EnumerateArray().Where(i => !i.GetProperty("isDefault").GetBoolean())
            .Select(i => (i.GetProperty("code").GetString(), i.GetProperty("state").GetString(), i.GetProperty("access").GetString(), i.GetProperty("folderName").GetString()))
            .ToList();
        Assert.Equal([("18", "active", "unchecked", Alpha), ("440", "active", "unchecked", Beta)], added);
    }

    [Fact]
    public async Task Should_RecordCompaniesScannedEvent_When_ScanRuns()
    {
        Folder(Alpha);

        await ScanAsync();

        Assert.Contains(_factory.Companies.AuditEvents, e => e.Kind == AuditActions.CompaniesScanned);
    }

    [Fact]
    public async Task Should_AddNothing_When_ScannedASecondTime()
    {
        Folder(Alpha);
        Folder(Beta);
        await ScanAsync();

        var second = await ScanAsync();

        Assert.Equal(0, second.GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task Should_CountNoUpdate_When_RescanChangesNothingInAMultiFileCompany()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha, "a.qbw", "b.qbw");
        var candidate = Item(await ListAsync(), Alpha).GetProperty("candidateFiles")[1].GetString();
        using var chosen = await PatchAsync(id, rowVersion, new { filePath = candidate });
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);

        var second = await ScanAsync();

        Assert.Equal(0, second.GetProperty("updated").GetInt32());
    }

    [Fact]
    public async Task Should_CopyRulesTemplateIntoCompanyFolder_When_CompanyIsAdded()
    {
        var (id, _) = await ScannedAsync(Alpha);

        var rules = _factory.Dir.Combine("data", "companies", id.ToString(), "rules.json");

        Assert.Equal(File.ReadAllBytes(Fixtures.SampleRules), File.ReadAllBytes(rules));
    }

    [Fact]
    public async Task Should_StartWithAnEmptyLedger_When_CompanyIsAdded()
    {
        var (id, _) = await ScannedAsync(Alpha);

        var ledger = _factory.Dir.Combine("data", "companies", id.ToString(), "ledger.json");

        Assert.True(File.Exists(ledger));
    }

    [Fact]
    public async Task Should_MarkMissing_When_SubfolderIsRemoved()
    {
        await ScannedAsync(Alpha);
        Directory.Delete(Path.Combine(_root, Alpha), true);

        var body = await ScanAsync();

        Assert.Equal("missing", Item(body, Alpha).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Should_KeepStatementsOfAMissingCompany_When_SubfolderIsRemoved()
    {
        var (id, _) = await ScannedAsync(Alpha);
        var statement = await DashboardSeed.SaveAsync(_factory.Store, "R1", T0);
        _factory.Store.SetCompany(statement, id, CompanySource.Chosen);
        Directory.Delete(Path.Combine(_root, Alpha), true);

        await ScanAsync();

        Assert.Equal(id, (await _factory.Store.GetAsync(statement, default))!.CompanyId);
    }

    [Fact]
    public async Task Should_ReportFoldersWithoutAQbwFile_When_Scanning()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Empty Folder"));

        var body = await ScanAsync();

        Assert.Equal(["Empty Folder"], body.GetProperty("noCompanyFile").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task Should_Answer503RootUnavailable_When_RootFolderDoesNotExist()
    {
        Directory.Delete(_root, true);

        using var client = Client();
        using var response = await client.PostAsync("/api/v2/companies/scan", null);

        var problem = await response.ReadProblemAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("company-root-unavailable", problem.GetProperty("type").GetString());
        Assert.Contains(_root, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Should_ChangeNoCompany_When_RootFolderDoesNotExist()
    {
        await ListAsync();
        var before = (await _factory.Companies.ListAsync(default)).Count;
        Directory.Delete(_root, true);

        using var client = Client();
        using var response = await client.PostAsync("/api/v2/companies/scan", null);

        Assert.Equal((before, 0), ((await _factory.Companies.ListAsync(default)).Count, _factory.Companies.AuditEvents.Count(e => e.Kind == AuditActions.CompaniesScanned)));
    }

    [Fact]
    public async Task Should_Answer503RootUnavailable_When_RootFolderIsNotSet()
    {
        using var host = _factory.WithSetting("Companies:RootFolder", "");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Users.TokenFor(DashboardRoles.Admin));

        using var response = await client.PostAsync("/api/v2/companies/scan", null);

        Assert.Equal("company-root-unavailable", (await response.ReadProblemAsync()).GetProperty("type").GetString());
    }

    // ---- list --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ReportNotConfigured_When_RootFolderIsEmpty()
    {
        using var host = _factory.WithSetting("Companies:RootFolder", "");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Users.TokenFor(DashboardRoles.Viewer));

        var body = await BodyAsync(await client.GetAsync("/api/v2/companies"));

        Assert.Equal(
            (false, 1, true),
            (body.GetProperty("rootConfigured").GetBoolean(), body.GetProperty("items").GetArrayLength(), body.GetProperty("items")[0].GetProperty("isDefault").GetBoolean()));
    }

    [Fact]
    public async Task Should_ReportRootReachable_When_RootFolderExists()
    {
        var body = await ListAsync();

        Assert.Equal((true, true), (body.GetProperty("rootConfigured").GetBoolean(), body.GetProperty("rootReachable").GetBoolean()));
    }

    [Fact]
    public async Task Should_ReportRootUnreachable_When_RootFolderIsGone()
    {
        Directory.Delete(_root, true);

        var body = await ListAsync();

        Assert.Equal((true, false), (body.GetProperty("rootConfigured").GetBoolean(), body.GetProperty("rootReachable").GetBoolean()));
    }

    [Fact]
    public async Task Should_ReturnNullLists_When_DefaultCompanyWasNeverSynced()
    {
        var body = await ListAsync();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("items")[0].GetProperty("lists").ValueKind);
    }

    [Fact]
    public async Task Should_IncludeRowVersion_When_ListingCompanies()
    {
        await ScannedAsync(Alpha);

        var body = await ListAsync();

        Assert.Equal(1, Item(body, Alpha).GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task Should_CountPendingStatements_When_ListingCompanies()
    {
        var (id, _) = await ScannedAsync(Alpha);
        foreach (var reference in new[] { "R1", "R2" })
        {
            _factory.Store.SetCompany(await DashboardSeed.SaveAsync(_factory.Store, reference, T0), id, CompanySource.Chosen);
        }

        var body = await ListAsync();

        Assert.Equal(2, Item(body, Alpha).GetProperty("waitingCount").GetInt32());
    }

    // ---- patch: file choice ------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ListBothCandidates_When_FolderHoldsTwoQbw()
    {
        await ScannedAsync(Alpha, "a.qbw", "b.qbw");

        var company = Item(await ListAsync(), Alpha);

        Assert.Equal(("ambiguous", 2), (company.GetProperty("state").GetString(), company.GetProperty("candidateFiles").GetArrayLength()));
    }

    [Fact]
    public async Task Should_ActivateCompany_When_PatchChoosesACandidateFile()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha, "a.qbw", "b.qbw");
        var candidate = Item(await ListAsync(), Alpha).GetProperty("candidateFiles")[1].GetString();

        using var response = await PatchAsync(id, rowVersion, new { filePath = candidate });

        var body = await BodyAsync(response);
        Assert.Equal((HttpStatusCode.OK, "active", candidate), (response.StatusCode, body.GetProperty("state").GetString(), body.GetProperty("filePath").GetString()));
    }

    [Fact]
    public async Task Should_SendNewRowVersionAsETag_When_PatchSucceeds()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha, "a.qbw", "b.qbw");
        var candidate = Item(await ListAsync(), Alpha).GetProperty("candidateFiles")[0].GetString();

        using var response = await PatchAsync(id, rowVersion, new { filePath = candidate });

        Assert.Equal($"\"{rowVersion + 1}\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Should_RecordCompanyUpdatedEvent_When_PatchSucceeds()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = new[] { "Takoma Park" } });

        Assert.Contains(_factory.Companies.AuditEvents, e => e.Kind == AuditActions.CompanyUpdated && e.CompanyId == id);
    }

    [Fact]
    public async Task Should_Answer412_When_PatchIfMatchIsStale()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion + 5, new { matchNames = new[] { "x" } });

        var problem = await response.ReadProblemAsync();
        Assert.Equal((HttpStatusCode.PreconditionFailed, "edited-by-someone-else"), (response.StatusCode, problem.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Should_Answer428_When_PatchHasNoIfMatch()
    {
        var (id, _) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, null, new { matchNames = new[] { "x" } });

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer400_When_FilePathIsNotACandidate()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha, "a.qbw", "b.qbw");

        using var response = await PatchAsync(id, rowVersion, new { filePath = Path.Combine(_root, "elsewhere.qbw") });

        var problem = await response.ReadProblemAsync();
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-request"), (response.StatusCode, problem.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Should_Answer404_When_PatchingUnknownCompany()
    {
        using var response = await PatchAsync(9999, 1, new { disabled = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- patch: disabled ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_DisableCompany_When_PatchedDisabled()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { disabled = true });

        Assert.Equal("disabled", (await BodyAsync(response)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Should_ReEnableAsActive_When_ChosenFileExists()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);
        using var off = await PatchAsync(id, rowVersion, new { disabled = true });

        using var on = await PatchAsync(id, rowVersion + 1, new { disabled = false });

        Assert.Equal("active", (await BodyAsync(on)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Should_ReEnableAsMissing_When_ChosenFileIsGone()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);
        using var off = await PatchAsync(id, rowVersion, new { disabled = true });
        File.Delete(Path.Combine(_root, Alpha, Alpha + ".qbw"));

        using var on = await PatchAsync(id, rowVersion + 1, new { disabled = false });

        Assert.Equal("missing", (await BodyAsync(on)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Should_ReEnableAsAmbiguous_When_NoFileWasChosenButCandidatesExist()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha, "a.qbw", "b.qbw");
        using var off = await PatchAsync(id, rowVersion, new { disabled = true });

        using var on = await PatchAsync(id, rowVersion + 1, new { disabled = false });

        Assert.Equal("ambiguous", (await BodyAsync(on)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Should_Answer400_When_DisablingTheDefaultCompany()
    {
        await ListAsync();
        var defaultCompany = (await _factory.Companies.ListAsync(default)).Single(c => c.IsDefault);

        using var response = await PatchAsync(defaultCompany.Id, defaultCompany.RowVersion, new { disabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- patch: match names ------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_TrimNamesAndDropBlanks_When_PatchSetsMatchNames()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = new[] { "  Takoma Park  ", "", "   ", "TP Grocery" } });

        Assert.Equal(["Takoma Park", "TP Grocery"], (await BodyAsync(response)).GetProperty("matchNames").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task Should_Accept50Names_When_PatchSetsMatchNames()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = Enumerable.Range(1, 50).Select(i => $"Name {i}") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer400_When_MoreThan50Names()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = Enumerable.Range(1, 51).Select(i => $"Name {i}") });

        var problem = await response.ReadProblemAsync();
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-request"), (response.StatusCode, problem.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Should_Accept200CharacterName_When_PatchSetsMatchNames()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = new[] { new string('a', 200) } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer400_When_ANameIsLongerThan200Characters()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = new[] { new string('a', 201) } });

        var problem = await response.ReadProblemAsync();
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-request"), (response.StatusCode, problem.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Should_Answer400_When_PatchBodyChangesNothing()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer400_When_PatchBodyHasAnUnknownField()
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { displayName = "Other" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- scopes (R-18 acceptance), one method per test group ---------------------------------------------------

    [Theory]
    [InlineData(DashboardRoles.Viewer, HttpStatusCode.OK)]
    [InlineData(DashboardRoles.Editor, HttpStatusCode.OK)]
    [InlineData(DashboardRoles.Admin, HttpStatusCode.OK)]
    public async Task Should_DecideListAccessByRole_When_GetCompanies(string role, HttpStatusCode expected)
    {
        using var client = Client(role);

        using var response = await client.GetAsync("/api/v2/companies");

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(DashboardRoles.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(DashboardRoles.Editor, HttpStatusCode.Forbidden)]
    [InlineData(DashboardRoles.Admin, HttpStatusCode.OK)]
    public async Task Should_DecideScanAccessByRole_When_PostScan(string role, HttpStatusCode expected)
    {
        using var client = Client(role);

        using var response = await client.PostAsync("/api/v2/companies/scan", null);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(DashboardRoles.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(DashboardRoles.Editor, HttpStatusCode.Forbidden)]
    [InlineData(DashboardRoles.Admin, HttpStatusCode.OK)]
    public async Task Should_DecidePatchAccessByRole_When_PatchCompany(string role, HttpStatusCode expected)
    {
        var (id, rowVersion) = await ScannedAsync(Alpha);

        using var response = await PatchAsync(id, rowVersion, new { matchNames = new[] { "x" } }, role);

        Assert.Equal(expected, response.StatusCode);
    }
}
