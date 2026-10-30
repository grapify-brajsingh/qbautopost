using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1502 (M15 §3, A-1, A-5): <c>GET /api/v2/quickbooks/lists/syncs</c> on the real host against a real PostgreSQL: a
/// scheduled ok, a dashboard ok by a signed-in editor and a skipped attempt, read back newest first with the editor's name.
/// </summary>
public sealed class V2QbListSyncHistoryDbTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string Syncs = "/api/v2/quickbooks/lists/syncs";

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

    [PostgresFact]
    public async Task Should_ListThreeAttemptsNewestFirstWithTheEditorAndTheCurrentOk_When_ReadFromPostgres()
    {
        Factory.Gateway.Company.Accounts.Add(new QbAccount { Name = "Utilities", Type = "Expense" });
        Factory.Gateway.Company.Vendors.Add("Home Depot");
        using var anonymous = Factory.CreateClient();
        var store = Factory.Services.GetRequiredService<IQbListsStore>();
        var t0 = DateTime.UtcNow.AddHours(-1);
        await store.RecordAsync(
            new ListSyncRun
            {
                Trigger = ListSyncTrigger.Schedule, StartedUtc = t0, FinishedUtc = t0.AddSeconds(3), Outcome = ListSyncOutcome.Ok,
                Accounts = 0, Vendors = 0, Customers = 0, MissingInRules = ["Office Supplies"],
            },
            QbLists.Empty with { SyncedUtc = t0 },
            CancellationToken.None);

        var editor = await AddUserAsync("eve@example.com", "Eve Editor", DashboardRoles.Editor);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync(client, "eve@example.com"));
        using (var sync = await client.PostAsync("/api/v2/quickbooks/lists/sync", null))
        {
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        }

        await store.RecordAsync(
            new ListSyncRun
            {
                Trigger = ListSyncTrigger.Schedule, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
                Outcome = ListSyncOutcome.Failed, Reason = ListSyncReasons.QuickBooksUnavailable, Error = "secret exception text",
            },
            null,
            CancellationToken.None);

        using var response = await client.GetAsync(Syncs);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["schedule", "dashboard", "schedule"], items.Select(i => i.GetProperty("trigger").GetString()));
        Assert.Equal(["failed", "ok", "ok"], items.Select(i => i.GetProperty("outcome").GetString()));
        Assert.Equal([false, true, false], items.Select(i => i.GetProperty("current").GetBoolean()));
        Assert.True(items[0].GetProperty("id").GetInt64() > items[1].GetProperty("id").GetInt64());
        Assert.Equal(editor.Id, items[1].GetProperty("requestedBy").GetProperty("id").GetString());
        Assert.Equal("Eve Editor", items[1].GetProperty("requestedBy").GetProperty("displayName").GetString());
        Assert.Equal(1, items[1].GetProperty("counts").GetProperty("accounts").GetInt32());
        Assert.Equal(1, items[1].GetProperty("counts").GetProperty("vendors").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("counts").ValueKind);
        Assert.DoesNotContain("secret exception text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Office Supplies", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Utilities", text, StringComparison.Ordinal);

        using var filtered = await client.GetAsync(Syncs + "?trigger=dashboard");
        var only = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, only.GetProperty("total").GetInt32());
        Assert.True(Assert.Single(only.GetProperty("items").EnumerateArray()).GetProperty("current").GetBoolean());

        using var unauthenticated = await anonymous.GetAsync(Syncs);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    private async Task<AppUser> AddUserAsync(string email, string name, string role)
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
            DisplayName = name,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private static async Task<string> SignInAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
