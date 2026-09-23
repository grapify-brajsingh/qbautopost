using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using Scalar.AspNetCore;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-913 (FR-A-18): the Scalar reference UI at <c>GET /api/v1/reference</c>.
/// <para>
/// Three properties matter more than the page looking right, and all three are about a documentation page living on
/// a machine that can post money into a real ledger: it is <b>off</b> unless somebody turned it on, it never puts a
/// live request one click away, and it fetches nothing from a third party — the QuickBooks server may have no
/// outbound internet at all, and a page that silently pulls script from a CDN is not something this app should do.
/// </para>
/// </summary>
public sealed class ReferenceUiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public ReferenceUiTests() => _host = _factory.WithSetting("Api:Reference:Enabled", "true");

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_Return404_When_TheReferenceIsDisabled()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync(OpenApi.ReferenceRoute);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_ServeThePage_When_TheReferenceIsEnabled()
    {
        using var client = Authorized(_host);

        using var response = await client.GetAsync(OpenApi.ReferenceRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Should_PointThePageAtOurDocument_When_TheReferenceIsEnabled()
    {
        var html = await PageAsync();

        var source = Regex.Match(html, "\"url\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;

        // Scalar writes the source **relative to the application root**, and its loader resolves it in the browser
        // with `new URL(url, origin + basePath + '/')`, where `basePath` is `window.location.pathname` with the
        // reference prefix removed — empty here, because this app is not hosted under a sub-path. Resolving it the
        // same way is as close to a browser as this suite gets; see §8 of the evidence file.
        var resolved = new Uri(new Uri("https://server/"), source).AbsolutePath;

        Assert.Equal(OpenApi.DocumentRoute, resolved);
    }

    /// <summary>
    /// The claim in <see cref="Should_FetchNothingFromAnotherHost_When_UseCdnIsFalse"/>, from the other side: the
    /// script the page asks for is one this host answers. A page whose only script 404s would pass a "no remote
    /// URL" check while showing a reader nothing at all.
    /// </summary>
    [Fact]
    public async Task Should_ServeTheScriptItself_When_UseCdnIsFalse()
    {
        using var client = Authorized(_host);

        using var response = await client.GetAsync(OpenApi.ReferenceRoute + "/scalar.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 10_000);
    }

    [Fact]
    public async Task Should_FetchNothingFromAnotherHost_When_UseCdnIsFalse()
    {
        var html = await PageAsync();

        var remote = Regex
            .Matches(html, "(?:src|href)\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Where(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                          || url.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(remote);
    }

    [Fact]
    public async Task Should_RefuseThePage_When_TheKeyLacksHealthRead()
    {
        var key = GiveClientAKey(ApiScopes.JobsRead);
        using var client = WithKey(_host, key);

        using var response = await client.GetAsync(OpenApi.ReferenceRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseThePage_When_ThereIsNoKeyAtAll()
    {
        using var client = _host.CreateClient();

        using var response = await client.GetAsync(OpenApi.ReferenceRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Should_HideTheRequestButtons_When_TryItIsNotAllowed()
    {
        var options = new ScalarOptions();

        ReferenceEndpoints.Configure(options, new ReferenceSettings { AllowTryIt = false });

        Assert.True(options.HideTestRequestButton);
        Assert.True(options.HideClientButton);
    }

    [Fact]
    public void Should_ShowTheRequestButtons_When_TryItIsAllowed()
    {
        var options = new ScalarOptions();

        ReferenceEndpoints.Configure(options, new ReferenceSettings { AllowTryIt = true });

        Assert.False(options.HideTestRequestButton);
        Assert.False(options.HideClientButton);
    }

    [Fact]
    public void Should_NameNoRemoteAsset_When_UseCdnIsFalse()
    {
        var options = new ScalarOptions();

        ReferenceEndpoints.Configure(options, new ReferenceSettings { UseCdn = false });

        Assert.True(string.IsNullOrEmpty(options.BundleUrl));
        Assert.False(options.DefaultFonts);
        Assert.False(options.Telemetry);
    }

    [Fact]
    public void Should_NameTheCdn_When_UseCdnIsTrue()
    {
        var options = new ScalarOptions();

        ReferenceEndpoints.Configure(options, new ReferenceSettings { UseCdn = true });

        Assert.Equal(ReferenceEndpoints.CdnBundleUrl, options.BundleUrl);
    }

    [Fact]
    public void Should_ShipWithTryItAndTheCdnOff_When_NobodyConfiguresThem()
    {
        var shipped = new ReferenceSettings();

        Assert.False(shipped.AllowTryIt);
        Assert.False(shipped.UseCdn);
    }

    private async Task<string> PageAsync()
    {
        using var client = Authorized(_host);
        using var response = await client.GetAsync(OpenApi.ReferenceRoute);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Handoff trap 13: <c>WithSetting</c> hands back the base factory, which has no authorized client.</summary>
    private static HttpClient Authorized(WebApplicationFactory<Program> host) => WithKey(host, ApiFactory.ApiKey);

    private static HttpClient WithKey(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        // Q-67: X-Api-Key is the only header ApiKeyMiddleware reads; Authorization: Bearer is not accepted yet.
        client.DefaultRequestHeaders.Add(ApiSettings.KeyHeader, key);
        return client;
    }

    private string GiveClientAKey(params string[] scopes)
    {
        var file = _factory.Dir.Combine("data", "clients.json");
        var key = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        new ApiClientStore(file).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "doc-reader",
                    Name = "Documentation reader",
                    KeyHash = secret.KeyHash,
                    KeySalt = secret.KeySalt,
                    Scopes = scopes,
                    CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });

        return key;
    }
}
