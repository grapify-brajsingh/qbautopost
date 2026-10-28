using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1109 (dashboard v2 spec 0 O-1, 17 D-0): mapping <c>/api/v2</c> leaves every v1 behaviour where it was. The
/// v1 post route still ignores <c>post: false</c> (spec 13.3, Q-98), the v1 refresh cookie still names
/// <c>/api/v1/auth</c>, and a v1 save still answers with a v1 <c>Location</c>.
/// </summary>
public sealed class V1UnchangedTests : IDisposable
{
    private const string PostFalseFixture = "v1-post-false-row.json";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Fixtures.PathOf("saved-requests-v2", name), Encoding.UTF8);

    [Fact]
    public async Task Should_IgnorePostFlag_When_PostedThroughV1()
    {
        var (saved, body) = await _host.SaveAsync(Fixture(PostFalseFixture));
        using (saved)
        {
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        }

        var id = body.GetProperty("id").GetInt64();
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "post");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var writes = string.Join("\n", _host.Factory.Gateway.Writes);
        Assert.Contains("SHELL OIL 4491", writes);
        Assert.Contains("99.10", writes);
    }

    [Fact]
    public async Task Should_KeepV1RefreshCookiePath_When_V2Mapped()
    {
        using var client = _host.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Viewer));

        using var response = await client.PostAsync("/api/v1/auth/logout", new StringContent(""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("path=/api/v1/auth;", cookie + ";");
    }

    [Fact]
    public async Task Should_AnswerV1Location_When_SavedThroughV1()
    {
        var (response, body) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"));

        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal($"/api/v1/saved-requests/{body.GetProperty("id").GetInt64()}", response.Headers.Location?.OriginalString);
        }
    }
}
