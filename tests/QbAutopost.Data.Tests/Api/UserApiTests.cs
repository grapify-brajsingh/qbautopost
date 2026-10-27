using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1011 (saved-requests spec 11.4, 10.11, 10.12, 5.2 user_audit, 5.3): user management and the bootstrap admin on the
/// real host against a real PostgreSQL. Every test gets a fresh, migrated database.
/// </summary>
public sealed class UserApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string Users = "/api/v1/users";
    private const string Login = "/api/v1/auth/login";
    private const string Refresh = "/api/v1/auth/refresh";
    private const string Me = "/api/v1/auth/me";
    private const string Cookie = "qbautopost_refresh";
    private const string TemporaryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private ApiFactory? _factory;
    private string? _connectionString;

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException(postgres.SkipReason);

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(_connectionString);
        _factory = ApiFactory.WithPostgres(_connectionString);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_connectionString is not null)
        {
            TestDatabase.Release(_connectionString);
        }

        return Task.CompletedTask;
    }

    // ---- create -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_Create201WithTemporaryPasswordOnce_When_AdminCreatesUser()
    {
        var admin = await AddUserAsync("root@example.com", DashboardRoles.Admin);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        using var created = await Send(client, HttpMethod.Post, Users, token, new { email = "New.Person@Example.com", displayName = "New Person", role = "editor" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var temporary = body.GetProperty("temporaryPassword").GetString()!;
        Assert.Equal(16, temporary.Length);
        Assert.All(temporary, c => Assert.Contains(c, TemporaryAlphabet));
        Assert.True(body.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal("editor", body.GetProperty("role").GetString());
        Assert.False(body.GetProperty("disabled").GetBoolean());
        Assert.Equal("New Person", body.GetProperty("displayName").GetString());
        var id = body.GetProperty("id").GetString()!;
        Assert.EndsWith($"{Users}/{id}", created.Headers.Location!.ToString());
        Assert.False(string.IsNullOrEmpty(created.Headers.ETag?.Tag));
        Assert.Contains("no-store", created.Headers.CacheControl!.ToString());

        using var read = await Send(client, HttpMethod.Get, $"{Users}/{id}", token);
        var text = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain("emporaryPassword", text);
        Assert.DoesNotContain(temporary, text);
        Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);

        var stored = await UserAsync(id);
        Assert.True(stored.MustChangePassword);
        Assert.Equal(admin.Id, stored.CreatedBy);
        Assert.DoesNotContain(temporary, stored.PasswordHash!);
        Assert.Equal(["editor"], await RolesOfAsync(id));
        var audit = await DbAsync(db => db.UserAudit.SingleAsync(a => a.UserId == id && a.Kind == "created"));
        Assert.Equal(admin.Id, audit.Actor);
        Assert.DoesNotContain(temporary, audit.Detail!);

        using var login = await LoginAsync(client, "new.person@example.com", temporary);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
    }

    [PostgresFact]
    public async Task Should_Return409_When_EmailExistsIgnoringCase()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        await AddUserAsync("ann@example.com", DashboardRoles.Viewer);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        using var response = await Send(client, HttpMethod.Post, Users, token, new { email = "ANN@Example.COM", displayName = "Other", role = "viewer" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email-exists", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(2, await DbAsync(db => db.Users.CountAsync()));
    }

    [PostgresFact]
    public async Task Should_Return400_When_CreateBodyIsInvalid()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        foreach (var body in new object[]
                 {
                     new { email = "a@example.com", displayName = "A", role = "owner" },
                     new { email = "a@example.com", displayName = "A" },
                     new { email = "not-an-email", displayName = "A", role = "viewer" },
                     new { email = "", displayName = "A", role = "viewer" },
                     new { email = "a@example.com", displayName = "  ", role = "viewer" },
                 })
        {
            using var response = await Send(client, HttpMethod.Post, Users, token, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(1, await DbAsync(db => db.Users.CountAsync()));
    }

    // ---- read ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_ReturnUserWithEtag_When_GetById()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        using var response = await Send(client, HttpMethod.Get, $"{Users}/{ann.Id}", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ann@example.com", body.GetProperty("email").GetString());
        Assert.Equal("editor", body.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastLoginUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lockedOutUntilUtc").ValueKind);
        Assert.Equal($"\"{(await UserAsync(ann.Id)).ConcurrencyStamp}\"", response.Headers.ETag!.Tag);
    }

    [PostgresFact]
    public async Task Should_Return404_When_UserUnknown()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"{Users}/nope"), (HttpMethod.Patch, $"{Users}/nope"),
                     (HttpMethod.Post, $"{Users}/nope/reset-password"), (HttpMethod.Post, $"{Users}/nope/unlock"),
                     (HttpMethod.Get, $"{Users}/nope/audit"),
                 })
        {
            using var response = await Send(client, method, path, token, method == HttpMethod.Patch ? new { displayName = "x" } : null, ifMatch: "\"x\"");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Should_FilterAndPage_When_ListingUsers()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        await AddUserAsync("ann@example.com", DashboardRoles.Editor, displayName: "Ann Lee");
        await AddUserAsync("bob@example.com", DashboardRoles.Viewer, displayName: "Bob 100%");
        await AddUserAsync("cy@example.com", DashboardRoles.Viewer, disabled: true, displayName: "Cy");
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        Assert.Equal(["ann@example.com", "bob@example.com", "cy@example.com", "root@example.com"], await EmailsAsync(client, token, ""));
        Assert.Equal(["bob@example.com", "cy@example.com"], await EmailsAsync(client, token, "?role=viewer"));
        Assert.Equal(["cy@example.com"], await EmailsAsync(client, token, "?disabled=true"));
        Assert.Equal(["ann@example.com", "bob@example.com", "root@example.com"], await EmailsAsync(client, token, "?disabled=false"));
        Assert.Equal(["ann@example.com"], await EmailsAsync(client, token, "?q=LEE"));
        Assert.Equal(["root@example.com"], await EmailsAsync(client, token, "?q=ROOT%40"));
        Assert.Equal(["bob@example.com"], await EmailsAsync(client, token, "?q=100%25"));
        Assert.Equal(["bob@example.com"], await EmailsAsync(client, token, "?q=%25")); // a literal percent, not a wildcard
        Assert.Equal(["cy@example.com", "root@example.com"], await EmailsAsync(client, token, "?page=2&pageSize=2"));

        using var paged = await Send(client, HttpMethod.Get, $"{Users}?page=2&pageSize=2", token);
        var body = await paged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, body.GetProperty("total").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(2, body.GetProperty("pageSize").GetInt32());

        foreach (var bad in new[] { "?role=owner", "?disabled=maybe", "?page=0", "?pageSize=0", "?pageSize=100000", "?page=x" })
        {
            using var response = await Send(client, HttpMethod.Get, Users + bad, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    // ---- patch --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_UpdateDisplayNameAndRotateEtag_When_PatchWithCurrentIfMatch()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        var etag = await EtagAsync(client, token, ann.Id);

        using var response = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, new { displayName = "Ann Renamed" }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ann Renamed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").GetString());
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        Assert.Equal("Ann Renamed", (await UserAsync(ann.Id)).DisplayName);
        Assert.Equal(["editor"], await RolesOfAsync(ann.Id));
    }

    [PostgresFact]
    public async Task Should_Return412_When_IfMatchStale()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        var stale = await EtagAsync(client, token, ann.Id);
        using var first = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, new { displayName = "First" }, stale);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, new { role = "viewer" }, stale);

        Assert.Equal(HttpStatusCode.PreconditionFailed, second.StatusCode);
        Assert.Equal("edited-by-someone-else", (await second.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(["editor"], await RolesOfAsync(ann.Id));
    }

    [PostgresFact]
    public async Task Should_Return428_When_IfMatchMissing()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        using var response = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, new { displayName = "x" });

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        Assert.Equal("precondition-required", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal("Ann Example", (await UserAsync(ann.Id)).DisplayName);
    }

    [PostgresFact]
    public async Task Should_Return400_When_PatchBodyIsEmptyOrInvalid()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        foreach (var body in new object[] { new { }, new { role = "owner" }, new { displayName = "  " } })
        {
            var etag = await EtagAsync(client, token, ann.Id);
            using var response = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, body, etag);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(["editor"], await RolesOfAsync(ann.Id));
    }

    [PostgresFact]
    public async Task Should_Return409LastAdmin_When_DemotingOnlyAdmin()
    {
        var root = await AddUserAsync("root@example.com", DashboardRoles.Admin);
        await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        var etag = await EtagAsync(client, token, root.Id);

        using var response = await Send(client, HttpMethod.Patch, $"{Users}/{root.Id}", token, new { role = "editor" }, etag);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("last-admin", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(["admin"], await RolesOfAsync(root.Id));
    }

    [PostgresFact]
    public async Task Should_NotCountDisabledAdmins_When_CheckingTheLastAdmin()
    {
        var root = await AddUserAsync("root@example.com", DashboardRoles.Admin);
        await AddUserAsync("gone@example.com", DashboardRoles.Admin, disabled: true);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        var etag = await EtagAsync(client, token, root.Id);

        using var response = await Send(client, HttpMethod.Patch, $"{Users}/{root.Id}", token, new { role = "viewer" }, etag);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("last-admin", (await response.ReadProblemAsync()).GetProperty("type").GetString());
    }

    [PostgresFact]
    public async Task Should_AllowDemotion_When_AnotherEnabledAdminRemains()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var second = await AddUserAsync("second@example.com", DashboardRoles.Admin);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        var etag = await EtagAsync(client, token, second.Id);

        using var response = await Send(client, HttpMethod.Patch, $"{Users}/{second.Id}", token, new { role = "viewer" }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["viewer"], await RolesOfAsync(second.Id));
        var audit = await DbAsync(db => db.UserAudit.SingleAsync(a => a.UserId == second.Id && a.Kind == "role-changed"));
        Assert.Contains("admin", audit.Detail!);
        Assert.Contains("viewer", audit.Detail!);
    }

    [PostgresFact]
    public async Task Should_Return409_When_AdminDisablesSelf()
    {
        var root = await AddUserAsync("root@example.com", DashboardRoles.Admin);
        await AddUserAsync("second@example.com", DashboardRoles.Admin);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        var etag = await EtagAsync(client, token, root.Id);

        using var response = await Send(client, HttpMethod.Patch, $"{Users}/{root.Id}", token, new { disabled = true }, etag);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("cannot-disable-self", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.False((await UserAsync(root.Id)).IsDisabled);
    }

    [PostgresFact]
    public async Task Should_LeaveAnEnabledAdmin_When_TwoAdminsDemoteEachOtherAtTheSameMoment()
    {
        using var client = Client();
        for (var round = 0; round < 8; round++)
        {
            var a = await AddUserAsync($"a{round}@example.com", DashboardRoles.Admin);
            var b = await AddUserAsync($"b{round}@example.com", DashboardRoles.Admin);
            var (tokenA, _) = await SignInAsync(client, a.Email!, Password);
            var (tokenB, _) = await SignInAsync(client, b.Email!, Password);
            var etagOfB = await EtagAsync(client, tokenA, b.Id);
            var etagOfA = await EtagAsync(client, tokenB, a.Id);
            // The other admins of earlier rounds would satisfy the rule, so they are put out of the way.
            await DbAsync(db => db.Users.Where(u => u.Id != a.Id && u.Id != b.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.IsDisabled, true)));
            var start = new TaskCompletionSource();

            async Task<HttpStatusCode> Demote(string token, string target, string etag)
            {
                await start.Task;
                using var response = await Send(client, HttpMethod.Patch, $"{Users}/{target}", token, new { role = "viewer" }, etag);
                return response.StatusCode;
            }

            var first = Demote(tokenA, b.Id, etagOfB);
            var second = Demote(tokenB, a.Id, etagOfA);
            start.SetResult();
            var statuses = await Task.WhenAll(first, second);

            var admins = (await RolesOfAsync(a.Id)).Concat(await RolesOfAsync(b.Id)).Count(r => r == "admin");
            Assert.True(admins >= 1, $"round {round}: no admin left; statuses {string.Join(",", statuses)}");
            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        }
    }

    // ---- revocation (R19) -----------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_RevokeTokensAndChangeStamp_When_RoleChanged()
    {
        await AssertSessionEndsAsync(async (client, token, ann) =>
        {
            var etag = await EtagAsync(client, token, ann.Id);
            return await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, new { role = "viewer" }, etag);
        });
    }

    [PostgresFact]
    public async Task Should_RevokeTokensAndChangeStamp_When_Disabled()
    {
        await AssertSessionEndsAsync(async (client, token, ann) =>
        {
            var etag = await EtagAsync(client, token, ann.Id);
            return await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", token, new { disabled = true }, etag);
        });
    }

    [PostgresFact]
    public async Task Should_RevokeTokensAndChangeStamp_When_PasswordReset()
    {
        await AssertSessionEndsAsync((client, token, ann) => Send(client, HttpMethod.Post, $"{Users}/{ann.Id}/reset-password", token));
    }

    [PostgresFact]
    public async Task Should_KeepSessionsAndOneRole_When_OnlyTheDisplayNameChanges()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (adminToken, _) = await SignInAsync(client, "root@example.com", Password);
        var (annToken, annRefresh) = await SignInAsync(client, "ann@example.com", Password);
        var etag = await EtagAsync(client, adminToken, ann.Id);

        using var patch = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", adminToken, new { displayName = "Renamed" }, etag);

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var me = await Send(client, HttpMethod.Get, Me, annToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var refreshed = await Send(client, HttpMethod.Post, Refresh, null, cookie: annRefresh);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [PostgresFact]
    public async Task Should_AcceptSignInAgain_When_DisabledUserIsEnabled()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor, disabled: true);
        using var client = Client();
        var (adminToken, _) = await SignInAsync(client, "root@example.com", Password);
        var etag = await EtagAsync(client, adminToken, ann.Id);

        using var patch = await Send(client, HttpMethod.Patch, $"{Users}/{ann.Id}", adminToken, new { disabled = false }, etag);

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Contains("enabled", await AuditKindsAsync(ann.Id));
        using var login = await LoginAsync(client, "ann@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>
    /// The user signs in and uses their token once (so the gate has cached them); the admin's action runs; the token
    /// minted before it is then refused at once (not after the 30 s cache), and the old refresh cookie gets 401.
    /// </summary>
    private async Task AssertSessionEndsAsync(Func<HttpClient, string, AppUser, Task<HttpResponseMessage>> act)
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (adminToken, _) = await SignInAsync(client, "root@example.com", Password);
        var (annToken, annRefresh) = await SignInAsync(client, "ann@example.com", Password);
        using (var before = await Send(client, HttpMethod.Get, Me, annToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        var stampBefore = (await UserAsync(ann.Id)).SecurityStamp;

        using (var response = await act(client, adminToken, ann))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.NotEqual(stampBefore, (await UserAsync(ann.Id)).SecurityStamp);
        using var after = await Send(client, HttpMethod.Get, Me, annToken);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using var refresh = await Send(client, HttpMethod.Post, Refresh, null, cookie: annRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var live = await DbAsync(db => db.RefreshTokens.CountAsync(t => t.UserId == ann.Id && t.RevokedUtc == null));
        Assert.Equal(0, live);
        Assert.Single(await RolesOfAsync(ann.Id));
    }

    // ---- reset-password, unlock, audit ----------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_ResetPasswordAndRequireChange_When_Reset()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);

        using var response = await Send(client, HttpMethod.Post, $"{Users}/{ann.Id}/reset-password", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var temporary = body.GetProperty("temporaryPassword").GetString()!;
        Assert.Equal(16, temporary.Length);
        Assert.True(body.GetProperty("mustChangePassword").GetBoolean());
        Assert.True((await UserAsync(ann.Id)).MustChangePassword);
        Assert.DoesNotContain(temporary, (await UserAsync(ann.Id)).PasswordHash!);
        using var old = await LoginAsync(client, "ann@example.com", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await LoginAsync(client, "ann@example.com", temporary);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.True((await fresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var audit = await DbAsync(db => db.UserAudit.SingleAsync(a => a.UserId == ann.Id && a.Kind == "password-reset"));
        Assert.DoesNotContain(temporary, audit.Detail ?? "");
    }

    [PostgresFact]
    public async Task Should_ClearLockout_When_Unlock()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        var ann = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        await DbAsync(db => db.Users.Where(u => u.Id == ann.Id).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(10))
            .SetProperty(u => u.AccessFailedCount, 3)));
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        using (var locked = await LoginAsync(client, "ann@example.com", Password))
        {
            Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        }

        using (var listed = await Send(client, HttpMethod.Get, $"{Users}/{ann.Id}", token))
        {
            Assert.NotEqual(JsonValueKind.Null, (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lockedOutUntilUtc").ValueKind);
        }

        using var response = await Send(client, HttpMethod.Post, $"{Users}/{ann.Id}/unlock", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lockedOutUntilUtc").ValueKind);
        var stored = await UserAsync(ann.Id);
        Assert.Null(stored.LockoutEnd);
        Assert.Equal(0, stored.AccessFailedCount);
        using var login = await LoginAsync(client, "ann@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("unlocked", await AuditKindsAsync(ann.Id));
    }

    [PostgresFact]
    public async Task Should_ListAuditRows_When_GetAudit()
    {
        var root = await AddUserAsync("root@example.com", DashboardRoles.Admin);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "root@example.com", Password);
        using var created = await Send(client, HttpMethod.Post, Users, token, new { email = "ann@example.com", displayName = "Ann", role = "editor" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var etag = await EtagAsync(client, token, id);
        using var changed = await Send(client, HttpMethod.Patch, $"{Users}/{id}", token, new { role = "viewer" }, etag);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var wrong = await LoginAsync(client, "ann@example.com", "wrong-password-here");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var response = await Send(client, HttpMethod.Get, $"{Users}/{id}/audit", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["login-failed", "role-changed", "created"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var roleChange = items[1];
        Assert.Equal(root.Id, roleChange.GetProperty("actor").GetString());
        Assert.Equal("Ann Example", roleChange.GetProperty("actorName").GetString());
        Assert.Equal("editor", roleChange.GetProperty("detail").GetProperty("from").GetString());
        Assert.Equal("viewer", roleChange.GetProperty("detail").GetProperty("to").GetString());
        Assert.Equal("anonymous", items[0].GetProperty("actor").GetString());

        using var paged = await Send(client, HttpMethod.Get, $"{Users}/{id}/audit?page=2&pageSize=2", token);
        var second = (await paged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("created", Assert.Single(second).GetProperty("kind").GetString());
    }

    // ---- who may call ---------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_Return403_When_EditorCallsUsers()
    {
        await AddUserAsync("ed@example.com", DashboardRoles.Editor);
        await AddUserAsync("vi@example.com", DashboardRoles.Viewer);
        using var client = Client();

        foreach (var email in new[] { "ed@example.com", "vi@example.com" })
        {
            var (token, _) = await SignInAsync(client, email, Password);
            foreach (var (method, path) in new[]
                     {
                         (HttpMethod.Get, Users), (HttpMethod.Post, Users), (HttpMethod.Get, $"{Users}/x"), (HttpMethod.Patch, $"{Users}/x"),
                         (HttpMethod.Post, $"{Users}/x/reset-password"), (HttpMethod.Post, $"{Users}/x/unlock"), (HttpMethod.Get, $"{Users}/x/audit"),
                     })
            {
                using var response = await Send(client, method, path, token, ifMatch: "\"x\"");
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        Assert.Equal(2, await DbAsync(db => db.Users.CountAsync()));
    }

    [PostgresFact]
    public async Task Should_Return403AndNotCreate_When_ApiKeyCallsUsers()
    {
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        using var client = Factory.CreateAuthorizedClient();

        using var list = await client.GetAsync(Users);
        using var create = await client.PostAsJsonAsync(Users, new { email = "x@example.com", displayName = "X", role = "admin" });

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(1, await DbAsync(db => db.Users.CountAsync()));
    }

    [PostgresFact]
    public async Task Should_Return401_When_NoCredentials()
    {
        using var client = Client();

        using var response = await client.GetAsync(Users);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- bootstrap ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Should_CreateAdminWithMustChange_When_TableEmptyAndSettingsPresent()
    {
        const string bootstrapPassword = "bootstrap-passphrase-1";
        using var host = BootstrapHost(Factory, "First.Admin@Example.com", bootstrapPassword);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var login = await LoginAsync(client, "first.admin@example.com", bootstrapPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var user = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");
        Assert.Equal("admin", user.GetProperty("role").GetString());
        Assert.True(user.GetProperty("mustChangePassword").GetBoolean());
        var stored = await DbAsync(db => db.Users.SingleAsync());
        Assert.True(stored.MustChangePassword);
        Assert.Equal(["admin"], await RolesOfAsync(stored.Id));
        Assert.Contains("created", await AuditKindsAsync(stored.Id));
        Assert.Contains(LogLines(Factory), l => l.Contains("First.Admin@Example.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(LogLines(Factory), l => l.Contains(bootstrapPassword, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Should_IgnoreAndWarn_When_UsersExist()
    {
        const string bootstrapPassword = "bootstrap-passphrase-1";
        await AddUserAsync("root@example.com", DashboardRoles.Admin);
        using var host = BootstrapHost(Factory, "first@example.com", bootstrapPassword);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var login = await LoginAsync(client, "first@example.com", bootstrapPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(1, await DbAsync(db => db.Users.CountAsync()));
        var warning = Assert.Single(LogLines(Factory), l => l.Contains("[WRN]", StringComparison.Ordinal) && l.Contains("BootstrapAdmin", StringComparison.Ordinal));
        Assert.DoesNotContain(bootstrapPassword, warning, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Should_CreateNothingAndLog_When_BootstrapPasswordIsTooShort()
    {
        using var host = BootstrapHost(Factory, "first@example.com", "bad-pw-1");
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var health = await client.GetAsync("/api/v1/health");

        Assert.Equal(0, await DbAsync(db => db.Users.CountAsync()));
        Assert.Contains(LogLines(Factory), l => l.Contains("[ERR]", StringComparison.Ordinal) && l.Contains("BootstrapAdmin", StringComparison.Ordinal));
        Assert.DoesNotContain(LogLines(Factory), l => l.Contains("bad-pw-1", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Should_NotCreateAnythingAndStillStart_When_BootstrapRunsBeforeTheDatabaseIsReady()
    {
        var emptyDatabase = await postgres.CreateDatabaseAsync(); // never migrated: the gate is not Ready
        try
        {
            using var factory = ApiFactory.WithPostgres(emptyDatabase);
            using var host = BootstrapHost(factory, "first@example.com", "bootstrap-passphrase-1");
            using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

            using var health = await client.GetAsync("/api/v1/health");

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Contains(LogLines(factory), l => l.Contains("[WRN]", StringComparison.Ordinal) && l.Contains("BootstrapAdmin", StringComparison.Ordinal));
        }
        finally
        {
            TestDatabase.Release(emptyDatabase);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static WebApplicationFactory<Program> BootstrapHost(ApiFactory factory, string email, string password) =>
        factory.WithSettings(new Dictionary<string, string?>
        {
            ["Auth:BootstrapAdmin:Email"] = email,
            ["Auth:BootstrapAdmin:Password"] = password,
        });

    private HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task<AppUser> AddUserAsync(string email, string role, bool disabled = false, string displayName = "Ann Example")
    {
        using var scope = Factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(role))
        {
            Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            IsDisabled = disabled,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private async Task<T> DbAsync<T>(Func<QbAutopostDbContext, Task<T>> query)
    {
        await using var db = await Factory.Services.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>().CreateDbContextAsync();
        return await query(db);
    }

    private Task<AppUser> UserAsync(string id) => DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<List<string>> AuditKindsAsync(string userId) =>
        DbAsync(db => db.UserAudit.Where(a => a.UserId == userId).OrderBy(a => a.Id).Select(a => a.Kind).ToListAsync());

    private Task<List<string>> RolesOfAsync(string userId) =>
        DbAsync(db => (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Name!).ToListAsync());

    private static async Task<string> EtagAsync(HttpClient client, string token, string id)
    {
        using var response = await Send(client, HttpMethod.Get, $"{Users}/{id}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static async Task<List<string>> EmailsAsync(HttpClient client, string token, string query)
    {
        using var response = await Send(client, HttpMethod.Get, Users + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()!).ToList();
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync(Login, new { email, password });

    private static async Task<(string AccessToken, string RefreshToken)> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await LoginAsync(client, email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return (body.GetProperty("accessToken").GetString()!, header[(Cookie.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)]);
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string path, string? token, object? body = null, string? ifMatch = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{Cookie}={cookie}");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        else if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("");
        }

        return client.SendAsync(request);
    }

    /// <summary>The lines of the operational log files of <paramref name="factory"/> ("[WRN]", "[ERR]" mark the level).</summary>
    private static List<string> LogLines(ApiFactory factory)
    {
        var folder = factory.Dir.Combine("data", "logs");
        return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .SelectMany(file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            })
            .ToList();
    }
}
