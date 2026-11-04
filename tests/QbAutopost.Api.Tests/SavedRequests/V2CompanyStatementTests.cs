using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1712 (M17 spec 4.6, 5.2, A-5, A-6, A-13): the company a statement belongs to, the <i>needs company</i> hold, the
/// set-company route, and duplicate rules scoped to one company. Unless a test says otherwise the host has a company root
/// with three active, approved companies: the default (1), Takoma (2) and S &amp; A (3).
/// </summary>
public sealed class V2CompanyStatementTests : IDisposable
{
    private const string Route = "/api/v2/saved-requests";
    private const string V1Route = "/api/v1/saved-requests";
    private const int DefaultId = 1;
    private const int TakomaId = 2;
    private const int SaId = 3;
    private const string TakomaPrinted = "TAKOMA PARK GROCERY STORE INC";
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

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

    /// <summary>The host with a company root; started on first use, so bootstrap makes the default company 1 first.</summary>
    private WebApplicationFactory<Program> Host
    {
        get
        {
            if (_host is null)
            {
                _host = _factory.WithSetting("Companies:RootFolder", Root);
                _host.CreateClient().Dispose();
            }

            return _host;
        }
    }

    /// <summary>Takoma and S &amp; A, active and approved, with their data folders; the default company stays as bootstrap made it.</summary>
    private void SeedCompanies(bool sa = true)
    {
        var folders = Host.Services.GetRequiredService<CompanyDataFolder>();
        _factory.Companies.Add(CompanyOf(TakomaId, "18", "Takoma Park Grocery Store Inc", TakomaFile));
        folders.Prepare(TakomaId);
        if (sa)
        {
            _factory.Companies.Add(CompanyOf(SaId, "440", "S & A Convenience Market LLC", SaFile));
            folders.Prepare(SaId);
        }
    }

    private static Company CompanyOf(int id, string code, string name, string file, CompanyState state = CompanyState.Active) =>
        new(id, $"{code}_{name}", code, name, file, [file], state, CompanyAccess.Approved, T0, null, null, null, [], false, T0, T0, 1);

    private HttpClient Editor(WebApplicationFactory<Program>? host = null) => Editor(host, out _);

    private HttpClient Editor(WebApplicationFactory<Program>? host, out string userId)
    {
        var token = _factory.Users.TokenFor(DashboardRoles.Editor);
        userId = _factory.Users.IdOf(token);
        var client = (host ?? Host).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>An API key with every scope: the emailed (n8n) path.</summary>
    private HttpClient N8n(WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? Host).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private static string Fixture(string folder, string name) => File.ReadAllText(Fixtures.PathOf(folder, name));

    private static string Statement => Fixture("saved-requests-v2", "post-false.json");

    /// <summary>The statement with the same reference, but other amounts: neither the same payload nor the same rows.</summary>
    private static string SameReferenceOtherRows()
    {
        var node = JsonNode.Parse(Statement)!.AsObject();
        node["controlTotal"] = 293.42m;
        node["transactions"]![0]!["amount"] = 194.32m;
        node["transactions"]![1]!["amount"] = 99.10m;
        node["transactions"]![1]!["date"] = "2026-08-06";
        return node.ToJsonString();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) => await SavedRequestTestHost.BodyOf(response);

    private static async Task<long> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyOf(response)).GetProperty("id").GetInt64();
    }

    private async Task<long> SaveByN8nAsync(string body, WebApplicationFactory<Program>? host = null, string route = Route)
    {
        using var client = N8n(host);
        using var response = await client.PostAsync(route, Json(body));
        return await CreatedIdAsync(response);
    }

    private async Task<Guid> SeedExtractionAsync(string userId, CompanyMatch? match)
    {
        var extracted = DateTime.UtcNow.AddMinutes(-5);
        var extraction = new StatementExtraction(
            Guid.NewGuid(), userId, "statement.pdf", 1000, new string('a', 64), 1, "~google/gemini-pro-latest",
            "2026-11-03.api-2", JsonNode.Parse(Fixture("saved-requests-v2", "upload-extraction.json"))!.AsObject(),
            extracted, extracted.AddHours(24), null)
        {
            Company = match,
        };
        await Store.CreateExtractionAsync(extraction, default);
        return extraction.Id;
    }

    private static string UploadBody(Guid extractionId, int? companyId = null)
    {
        var node = JsonNode.Parse(Fixture("saved-requests-v2", "upload-unedited.json"))!.AsObject();
        var upload = new JsonObject { ["extractionId"] = extractionId.ToString() };
        if (companyId is { } id)
        {
            upload["companyId"] = id;
        }

        node["upload"] = upload;
        return node.ToJsonString();
    }

    private async Task<HttpResponseMessage> UploadAsync(CompanyMatch? match, int? companyId = null)
    {
        using var client = Editor(null, out var userId);
        var extractionId = await SeedExtractionAsync(userId, match);
        return await client.PostAsync(Route, Json(UploadBody(extractionId, companyId)));
    }

    private static CompanyMatch Matched(int id) => new(CompanyMatchKind.Matched, TakomaPrinted, null, id, [id]);

    private async Task<(HttpResponseMessage Response, JsonElement Body)> GetAsync(long id, WebApplicationFactory<Program>? host = null)
    {
        using var client = Editor(host);
        var response = await client.GetAsync($"{Route}/{id}");
        return (response, await BodyOf(response));
    }

    private async Task<JsonElement> DetailAsync(long id, WebApplicationFactory<Program>? host = null)
    {
        var (response, body) = await GetAsync(id, host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Dispose();
        return body;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ActionAsync(
        long id, string action, WebApplicationFactory<Program>? host = null, string route = Route)
    {
        using var client = Editor(host);
        using var response = await client.PostAsync($"{route}/{id}/{action}", null);
        return (response.StatusCode, await BodyOf(response));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SetCompanyAsync(long id, int companyId, int? ifMatch = null)
    {
        var rowVersion = ifMatch ?? (await Store.GetAsync(id, default))!.RowVersion;
        using var client = Editor();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Route}/{id}/company")
        {
            Content = Json($$"""{"companyId":{{companyId}}}"""),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await BodyOf(response));
    }

    private static string[] Actions(JsonElement view) =>
        view.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!).ToArray();

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, default))!;

    private async Task RecordListsAsync(int companyId, params string[] vendors)
    {
        var catalog = Host.Services.GetRequiredService<IQbListsCatalog>();
        var now = DateTime.UtcNow;
        await catalog.StoreFor(companyId).RecordAsync(
            new ListSyncRun { Trigger = ListSyncTrigger.Dashboard, Outcome = ListSyncOutcome.Ok, StartedUtc = now, FinishedUtc = now },
            new QbLists
            {
                SyncedUtc = now,
                Accounts = [new QbAccount { Name = "Chase Checking 4521" }, new QbAccount { Name = "Repairs and Maintenance" }],
                Vendors = [.. vendors],
            },
            CancellationToken.None);
    }

    // ---- upload save: chosen, matched, none (spec 4.6) -------------------------------------------------------------

    [Fact]
    public async Task Should_SaveUnassignedAndHold_When_PdfHeaderHasNoRecognisableName()
    {
        SeedCompanies();
        using var saved = await UploadAsync(CompanyMatch.NoneFor(null, null));
        var id = await CreatedIdAsync(saved);

        var detail = await DetailAsync(id);
        var (validateStatus, validate) = await ActionAsync(id, "validate");
        var (postStatus, post) = await ActionAsync(id, "post");

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("company").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("companySource").ValueKind);
        Assert.Contains("set-company", Actions(detail));
        Assert.DoesNotContain("validate", Actions(detail));
        Assert.DoesNotContain("post", Actions(detail));
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (validateStatus, postStatus));
        Assert.Equal("company-required", validate.GetProperty("type").GetString());
        Assert.Equal("company-required", post.GetProperty("type").GetString());
        Assert.Empty(await Store.AttemptsAsync(id, default));
    }

    [Fact]
    public async Task Should_AssignTheMatchedCompany_When_UploadMatched()
    {
        SeedCompanies();
        using var saved = await UploadAsync(Matched(TakomaId));
        var id = await CreatedIdAsync(saved);

        var detail = await DetailAsync(id);

        Assert.Equal(TakomaId, detail.GetProperty("company").GetProperty("id").GetInt32());
        Assert.Equal("18", detail.GetProperty("company").GetProperty("code").GetString());
        Assert.Equal("Takoma Park Grocery Store Inc", detail.GetProperty("company").GetProperty("displayName").GetString());
        Assert.Equal("matched", detail.GetProperty("companySource").GetString());
        Assert.Equal("matched", detail.GetProperty("companyMatch").GetProperty("match").GetString());
        Assert.Equal(TakomaPrinted, detail.GetProperty("companyMatch").GetProperty("printedName").GetString());
        Assert.Equal([TakomaId], detail.GetProperty("companyMatch").GetProperty("candidates").EnumerateArray().Select(c => c.GetInt32()));
    }

    [Fact]
    public async Task Should_AssignTheChosenCompany_When_UploadSendsCompanyId()
    {
        SeedCompanies();
        using var saved = await UploadAsync(Matched(TakomaId), companyId: SaId);
        var id = await CreatedIdAsync(saved);

        var detail = await DetailAsync(id);

        Assert.Equal(SaId, detail.GetProperty("company").GetProperty("id").GetInt32());
        Assert.Equal("chosen", detail.GetProperty("companySource").GetString());
        Assert.Equal("matched", detail.GetProperty("companyMatch").GetProperty("match").GetString());
    }

    [Theory]
    [InlineData(99, null)]
    [InlineData(4, CompanyState.Missing)]
    [InlineData(4, CompanyState.Disabled)]
    public async Task Should_Answer400_When_UploadCompanyIdIsMissingOrDisabled(int companyId, CompanyState? state)
    {
        SeedCompanies();
        if (state is { } s)
        {
            _factory.Companies.Add(CompanyOf(companyId, "77", "Gone Market", Path.Combine(Root, "Gone.QBW"), s));
        }

        using var response = await UploadAsync(Matched(TakomaId), companyId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", (await BodyOf(response)).GetProperty("type").GetString());
        Assert.Equal(0, (await Store.ListAsync(new SavedRequestQuery([], null, null, null, null, null, null, "-created", 1, 10), default)).Total);
    }

    // ---- n8n / API save: auto-assign or nothing (spec 4.6, A-13) ---------------------------------------------------

    [Fact]
    public async Task Should_AssignDefaultAndPost_When_RootFolderIsEmpty()
    {
        var id = await SaveByN8nAsync(Statement, _factory);

        var stored = await StoredAsync(id);
        var (status, body) = await ActionAsync(id, "post", _factory);

        Assert.Equal((DefaultId, CompanySource.Default), (stored.CompanyId, stored.CompanySource));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("posted", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Should_AssignTheOnlyActiveCompany_When_OneCompanyIsActive()
    {
        SeedCompanies(sa: false);
        // The default company's folder is gone (a scan marks it missing), so Takoma is the only active company.
        await _factory.Companies.ApplyScanAsync(new ScanPlan([], [], [DefaultId], []), "admin", T0, CancellationToken.None);
        Assert.Equal(CompanyState.Missing, (await _factory.Companies.GetAsync(DefaultId, CancellationToken.None))!.State);

        var id = await SaveByN8nAsync(Statement);

        var stored = await StoredAsync(id);
        Assert.Equal((TakomaId, CompanySource.Default), (stored.CompanyId, stored.CompanySource));
    }

    [Fact]
    public async Task Should_LeaveEmailedStatementUnassigned_When_TwoCompaniesAreActive()
    {
        SeedCompanies();

        var id = await SaveByN8nAsync(Statement);

        var stored = await StoredAsync(id);
        Assert.Equal((null, null), (stored.CompanyId, stored.CompanySource));
        Assert.Equal(ValidationState.Unavailable, stored.ValidationV2!.State);
    }

    // ---- set-company (spec 4.6, A-6) ------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ClearValidationAndAudit_When_CompanyIsSet()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);

        var (status, body) = await SetCompanyAsync(id, TakomaId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TakomaId, body.GetProperty("company").GetProperty("id").GetInt32());
        Assert.Equal("chosen", body.GetProperty("companySource").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("validation").ValueKind);
        Assert.Contains("validate", Actions(body));
        var assigned = (await Store.EventsAsync(id, default)).Last();
        Assert.Equal("company-assigned", assigned.Kind);
        Assert.Equal(TakomaId, (int)assigned.Detail!["companyId"]!);
        Assert.Null(assigned.Detail["from"]);
        Assert.Equal(TakomaId, (int)assigned.Detail["to"]!["id"]!);
        Assert.Equal("18", (string)assigned.Detail["to"]!["code"]!);
        Assert.Equal("Takoma Park Grocery Store Inc", (string)assigned.Detail["to"]!["displayName"]!);
    }

    [Fact]
    public async Task Should_NameTheOldCompany_When_CompanyIsChanged()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        Assert.Equal(HttpStatusCode.OK, (await SetCompanyAsync(id, TakomaId)).Status);

        var (status, _) = await SetCompanyAsync(id, SaId);

        Assert.Equal(HttpStatusCode.OK, status);
        var assigned = (await Store.EventsAsync(id, default)).Last();
        Assert.Equal(TakomaId, (int)assigned.Detail!["from"]!["id"]!);
        Assert.Equal(SaId, (int)assigned.Detail["to"]!["id"]!);
    }

    [Fact]
    public async Task Should_ValidateWithThatCompanysListsAndRules_When_CompanyIsSet()
    {
        SeedCompanies();
        await RecordListsAsync(TakomaId, "Home Depot");
        await RecordListsAsync(SaId, "Home Depot", "NEVER HEARD OF THEM");
        var payload = Fixture("sync-post", SyncPostFixtures.HeldUnknownPayee);
        var takoma = await SaveByN8nAsync(payload);
        var sa = await SaveByN8nAsync(payload);
        await SetCompanyAsync(takoma, TakomaId);
        await SetCompanyAsync(sa, SaId);

        var (takomaStatus, takomaBody) = await ActionAsync(takoma, "validate");
        var (saStatus, saBody) = await ActionAsync(sa, "validate");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (takomaStatus, saStatus));
        static string[] Vendors(JsonElement body) =>
            body.GetProperty("validation").GetProperty("unknownNames").GetProperty("vendors").EnumerateArray()
                .Select(v => v.GetString()!).ToArray();
        Assert.Equal(["NEVER HEARD OF THEM"], Vendors(takomaBody));
        Assert.Empty(Vendors(saBody));
    }

    [Fact]
    public async Task Should_Answer409_When_SettingCompanyWhilePosting()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        await Store.TryClaimAsync(id, null, "user:someone", false, DateTime.UtcNow, default);

        var (status, body) = await SetCompanyAsync(id, TakomaId);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("not-editable", body.GetProperty("type").GetString());
        Assert.Null((await StoredAsync(id)).CompanyId);
    }

    [Fact]
    public async Task Should_Answer412_When_SetCompanyIfMatchIsStale()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        var stale = (await StoredAsync(id)).RowVersion - 1;

        var (status, body) = await SetCompanyAsync(id, TakomaId, ifMatch: stale);

        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal("edited-by-someone-else", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_Answer428_When_SetCompanyHasNoIfMatch()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        using var client = Editor();

        using var response = await client.PostAsync($"{Route}/{id}/company", Json($$"""{"companyId":{{TakomaId}}}"""));

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Theory]
    [InlineData(99, null)]
    [InlineData(4, CompanyState.Missing)]
    [InlineData(4, CompanyState.Disabled)]
    public async Task Should_Answer400_When_SetCompanyNamesAnUnknownMissingOrDisabledCompany(int companyId, CompanyState? state)
    {
        SeedCompanies();
        if (state is { } s)
        {
            _factory.Companies.Add(CompanyOf(companyId, "77", "Gone Market", Path.Combine(Root, "Gone.QBW"), s));
        }

        var id = await SaveByN8nAsync(Statement);

        var (status, body) = await SetCompanyAsync(id, companyId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
        Assert.Null((await StoredAsync(id)).CompanyId);
    }

    [Fact]
    public async Task Should_Answer409NotReadyWithAnAttempt_When_CompanyStopsBeingReadyAfterTheClaim()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        await SetCompanyAsync(id, TakomaId);
        Store.BeforeNextClaim = () => _factory.Companies
            .UpdateAsync(TakomaId, 1, new CompanyPatch(null, null, true), "admin", T0, CancellationToken.None)
            .GetAwaiter().GetResult();

        var (status, body) = await ActionAsync(id, "post");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-not-ready", body.GetProperty("type").GetString());
        Assert.Equal("disabled", body.GetProperty("state").GetString());
        var attempt = Assert.Single(await Store.AttemptsAsync(id, default));
        Assert.Equal(AttemptOutcome.Unavailable, attempt.Outcome);
        Assert.Equal(409, attempt.HttpStatus);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
        Assert.Empty(_factory.Gateway.Requests);
    }

    // ---- list and stats filter (spec 5.2) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Should_Answer400_When_CompanyFilterIsMalformed(string value)
    {
        SeedCompanies();
        using var client = Editor();

        using var list = await client.GetAsync($"{Route}?company={value}");
        using var stats = await client.GetAsync($"{Route}/stats?company={value}");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (list.StatusCode, stats.StatusCode));
    }

    [Fact]
    public async Task Should_Answer400_When_CompanyFilterIsRepeated()
    {
        SeedCompanies();
        using var client = Editor();

        using var list = await client.GetAsync($"{Route}?company=2&company=3");

        Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
    }

    [Fact]
    public async Task Should_ReturnEmptyPage_When_CompanyFilterIsUnknownId()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        await SetCompanyAsync(id, TakomaId);
        using var client = Editor();

        using var response = await client.GetAsync($"{Route}?company=999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Should_FilterNeedsCompany_When_CompanyIsNone()
    {
        SeedCompanies();
        var unassigned = await SaveByN8nAsync(Statement);
        var assigned = await SaveByN8nAsync(SameReferenceOtherRows());
        await SetCompanyAsync(assigned, TakomaId);
        using var client = Editor();

        using var list = await client.GetAsync($"{Route}?company=none");
        using var stats = await client.GetAsync($"{Route}/stats");
        using var takomaStats = await client.GetAsync($"{Route}/stats?company={TakomaId}");

        var row = Assert.Single((await BodyOf(list)).GetProperty("items").EnumerateArray());
        var all = await BodyOf(stats);
        var takoma = await BodyOf(takomaStats);
        Assert.Equal(unassigned, row.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("company").ValueKind);
        Assert.Contains("set-company", Actions(row));
        Assert.Equal((2, 1), (all.GetProperty("total").GetInt32(), all.GetProperty("needsCompany").GetInt32()));
        Assert.Equal((1, 0), (takoma.GetProperty("total").GetInt32(), takoma.GetProperty("needsCompany").GetInt32()));
    }

    [Fact]
    public async Task Should_ShowCompanyAndSource_When_Listing()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement);
        await SetCompanyAsync(id, TakomaId);
        using var client = Editor();

        using var list = await client.GetAsync($"{Route}?company={TakomaId}");

        var row = Assert.Single((await BodyOf(list)).GetProperty("items").EnumerateArray());
        Assert.Equal(TakomaId, row.GetProperty("company").GetProperty("id").GetInt32());
        Assert.Equal("chosen", row.GetProperty("companySource").GetString());
        Assert.False(row.TryGetProperty("companyMatch", out _));
    }

    // ---- duplicates (spec 4.6) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_CompareReferencesOnlyWithinACompany_When_DetectingDuplicates()
    {
        SeedCompanies();
        var first = await SaveByN8nAsync(Statement);
        await SetCompanyAsync(first, TakomaId);
        var second = await SaveByN8nAsync(SameReferenceOtherRows());
        var unassigned = (await StoredAsync(second)).DuplicateReasons;

        await SetCompanyAsync(second, SaId);
        var otherCompany = (await StoredAsync(second)).DuplicateReasons;
        await SetCompanyAsync(second, TakomaId);
        var sameCompany = await StoredAsync(second);

        Assert.Empty(unassigned);
        Assert.Empty(otherCompany);
        Assert.Equal([DuplicateReasons.SameReference], sameCompany.DuplicateReasons);
        Assert.Equal(first, sameCompany.DuplicateOfId);
    }

    // ---- v1 (plan conflict 8) --------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_Answer409CompanyRequired_When_V1PostOfUnassignedStatement()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement, route: V1Route);

        var (status, body) = await ActionAsync(id, "post", route: V1Route);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-required", body.GetProperty("type").GetString());
        Assert.Empty(await Store.AttemptsAsync(id, default));
    }

    [Fact]
    public async Task Should_ShowNoCompanyFields_When_V1DetailRead()
    {
        SeedCompanies();
        var id = await SaveByN8nAsync(Statement, route: V1Route);
        using var client = Editor();

        using var response = await client.GetAsync($"{V1Route}/{id}");

        var body = await BodyOf(response);
        Assert.False(body.TryGetProperty("company", out _));
        Assert.False(body.TryGetProperty("companySource", out _));
        Assert.DoesNotContain("set-company", Actions(body));
    }
}
