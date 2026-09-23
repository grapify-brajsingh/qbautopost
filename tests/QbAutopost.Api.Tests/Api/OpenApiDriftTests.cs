using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-913 (FR-A-18, api-v1 §11.6): the hand-authored <c>wwwroot/openapi.json</c> and the routes it claims to
/// describe, checked against each other.
/// <para>
/// .NET 8 has no built-in OpenAPI generator and Swashbuckle is excluded by the owner, so the document is written by
/// hand — which means it can drift. This is the test that stops it: a route nobody documented fails, an operation
/// nobody mapped fails, and an operation without a summary, a response schema or its scope fails. The scope is not
/// merely *present*: it must equal what <see cref="ApiRoutes.ScopeFor"/> actually enforces, so the page cannot tell
/// a caller they need one scope while the middleware demands another.
/// </para>
/// </summary>
public sealed class OpenApiDriftTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public OpenApiDriftTests() => _host = _factory.WithSetting("Api:Reference:Enabled", "true");

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_DocumentEveryMappedRoute_When_TheDocumentIsComparedWithTheRoutes()
    {
        using var document = await DocumentAsync();

        var undocumented = MappedRoutes()
            .Where(route => !IsDocumented(document, route))
            .Select(route => $"{route.Method} {route.Path}")
            .ToList();

        Assert.Empty(undocumented);
    }

    [Fact]
    public async Task Should_MapEveryDocumentedOperation_When_TheDocumentIsComparedWithTheRoutes()
    {
        using var document = await DocumentAsync();
        var mapped = MappedRoutes().ToHashSet();

        var unmapped = Operations(document)
            .Where(operation => !mapped.Contains((operation.Method, operation.Path)))
            .Select(operation => $"{operation.Method} {operation.Path}")
            .ToList();

        Assert.Empty(unmapped);
    }

    [Fact]
    public async Task Should_GiveEveryOperationASummary_When_TheDocumentIsRead()
    {
        using var document = await DocumentAsync();

        var missing = Operations(document)
            .Where(o => !o.Value.TryGetProperty("summary", out var summary)
                        || string.IsNullOrWhiteSpace(summary.GetString()))
            .Select(o => $"{o.Method} {o.Path}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Should_GiveEveryOperationASuccessResponseSchema_When_TheDocumentIsRead()
    {
        using var document = await DocumentAsync();

        var missing = Operations(document).Where(o => !HasSuccessSchema(o.Value)).Select(o => $"{o.Method} {o.Path}").ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Should_DeclareTheScopeTheMiddlewareEnforces_When_TheDocumentIsRead()
    {
        using var document = await DocumentAsync();

        var wrong = new List<string>();
        foreach (var operation in Operations(document))
        {
            var enforced = ApiRoutes.ScopeFor(operation.Method, operation.Path) ?? OpenApi.NoScope;
            var documented = operation.Value.TryGetProperty(OpenApi.ScopeProperty, out var scope) ? scope.GetString() : null;
            if (documented != enforced)
            {
                wrong.Add($"{operation.Method} {operation.Path}: documented '{documented}', enforced '{enforced}'");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task Should_DeclareABearerSecurityScheme_When_TheDocumentIsRead()
    {
        using var document = await DocumentAsync();

        var scheme = Schemes(document).GetProperty(OpenApi.BearerScheme);

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    /// <summary>
    /// The one thing a drift test cannot learn from the route table: which header the key travels in. FR-A-18 asks
    /// the document to declare <c>Authorization: Bearer</c>, and api-v1 §2.2 calls it the preferred form — but
    /// <c>ApiKeyMiddleware</c> reads <see cref="ApiSettings.KeyHeader"/> and nothing else (Q-67). A document that
    /// asked every reader for a header the app ignores would be worse than no document, so what the page *offers* is
    /// the scheme that works, and the bearer scheme is declared beside it with its status written down.
    /// </summary>
    [Fact]
    public async Task Should_DeclareTheSchemeThatActuallyWorks_When_TheDocumentIsRead()
    {
        using var document = await DocumentAsync();

        var offered = document.RootElement.GetProperty("security")[0].EnumerateObject().Single().Name;
        var scheme = Schemes(document).GetProperty(offered);

        Assert.Equal(OpenApi.ApiKeyScheme, offered);
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal(ApiSettings.KeyHeader, scheme.GetProperty("name").GetString());
    }

    private static JsonElement Schemes(JsonDocument document) =>
        document.RootElement.GetProperty("components").GetProperty("securitySchemes");

    [Fact]
    public async Task Should_OpenOnlyLivenessAndReadiness_When_TheDocumentDeclaresPerOperationSecurity()
    {
        using var document = await DocumentAsync();

        var wrong = new List<string>();
        foreach (var operation in Operations(document))
        {
            var open = ApiRoutes.ScopeFor(operation.Method, operation.Path) is null;
            var declaresNoSecurity = operation.Value.TryGetProperty("security", out var security)
                                     && security.GetArrayLength() == 0;
            if (open != declaresNoSecurity)
            {
                wrong.Add($"{operation.Method} {operation.Path}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task Should_ServeTheDocument_When_ReferenceIsEnabled()
    {
        using var client = Authorized(_host);

        using var response = await client.GetAsync(OpenApi.DocumentRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Should_NotServeTheDocument_When_ReferenceIsDisabled()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync(OpenApi.DocumentRoute);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseTheDocument_When_TheKeyLacksHealthRead()
    {
        var key = GiveClientAKey(ApiScopes.JobsRead);
        using var client = WithKey(_host, key);

        using var response = await client.GetAsync(OpenApi.DocumentRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_ServeTheDocument_When_TheKeyHoldsHealthRead()
    {
        var key = GiveClientAKey(ApiScopes.HealthRead);
        using var client = WithKey(_host, key);

        using var response = await client.GetAsync(OpenApi.DocumentRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Should_ShipDisabled_When_NobodyConfiguresTheReference() =>
        Assert.False(new QbAutopost.Api.Configuration.ReferenceSettings().Enabled);

    /// <summary>The document as the host serves it — the file a caller would read, not the file on disk.</summary>
    private async Task<JsonDocument> DocumentAsync()
    {
        using var client = Authorized(_host);
        using var response = await client.GetAsync(OpenApi.DocumentRoute);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Every <c>/api/v1</c> route the host actually mapped. The reference UI's own sub-paths are left out: Scalar
    /// maps its page and its assets under one prefix, and an asset is not an operation a caller calls.
    /// </summary>
    private IEnumerable<(string Method, string Path)> MappedRoutes()
    {
        var source = _host.Services.GetRequiredService<EndpointDataSource>();
        var routes = new HashSet<(string, string)>();
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var path = Normalise(endpoint.RoutePattern.RawText ?? string.Empty);
            if (!path.StartsWith(ApiRoutes.V1Prefix, StringComparison.OrdinalIgnoreCase) || IsReferenceAsset(path))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                routes.Add((method.ToUpperInvariant(), path));
            }
        }

        return routes;
    }

    /// <summary>
    /// A route pattern as the document spells it: no trailing slash (<c>/api/v1/jobs/</c> and <c>/api/v1/jobs</c> are
    /// one route), and an optional trailing parameter dropped, because a route that ends in <c>{x?}</c> also answers
    /// the path without it — which is the path a reader would type.
    /// </summary>
    private static string Normalise(string raw)
    {
        var path = "/" + raw.Trim('/');
        if (path.EndsWith("?}", StringComparison.Ordinal) && path.LastIndexOf("/{", StringComparison.Ordinal) is var cut and > 0)
        {
            path = path[..cut];
        }

        return path;
    }

    private static bool IsReferenceAsset(string path) =>
        ApiRoutes.IsReference(path)
        && !string.Equals(path, OpenApi.ReferenceRoute, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(path, OpenApi.DocumentRoute, StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumented(JsonDocument document, (string Method, string Path) route) =>
        Operations(document).Any(o => o.Method == route.Method && o.Path == route.Path);

    private static readonly HashSet<string> Methods =
        new(StringComparer.Ordinal) { "get", "put", "post", "delete", "patch", "head", "options", "trace" };

    private static IEnumerable<(string Path, string Method, JsonElement Value)> Operations(JsonDocument document)
    {
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                // A path item may also carry keys that are not operations ("parameters", "summary"); only the
                // HTTP methods describe something a caller calls.
                if (Methods.Contains(operation.Name))
                {
                    yield return (path.Name, operation.Name.ToUpperInvariant(), operation.Value);
                }
            }
        }
    }

    private static bool HasSuccessSchema(JsonElement operation)
    {
        if (!operation.TryGetProperty("responses", out var responses))
        {
            return false;
        }

        foreach (var response in responses.EnumerateObject())
        {
            if (!response.Name.StartsWith('2') || !response.Value.TryGetProperty("content", out var content))
            {
                continue;
            }

            if (content.EnumerateObject().Any(media => media.Value.TryGetProperty("schema", out _)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Handoff trap 13: <c>WithSetting</c> hands back the base factory, which has no authorized client.</summary>
    private static HttpClient Authorized(WebApplicationFactory<Program> host) => WithKey(host, ApiFactory.ApiKey);

    /// <summary>
    /// The header the middleware actually reads. api-v1 §2.2 calls <c>Authorization: Bearer</c> the preferred form,
    /// but <c>ApiKeyMiddleware</c> reads <c>X-Api-Key</c> and nothing else — see Q-67, and
    /// <see cref="Should_DeclareTheSchemeThatActuallyWorks_When_TheDocumentIsRead"/>, which is why the document does
    /// not send a reader down a road this build refuses.
    /// </summary>
    private static HttpClient WithKey(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiSettings.KeyHeader, key);
        return client;
    }

    /// <summary>Issues one caller with exactly the scopes named, and returns their key.</summary>
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

/// <summary>Names this suite shares with <see cref="ReferenceUiTests"/>; the routes and the two document keywords.</summary>
internal static class OpenApi
{
    public const string DocumentRoute = ApiRoutes.V1Prefix + ApiRoutes.OpenApiPath;
    public const string ReferenceRoute = ApiRoutes.V1Prefix + ApiRoutes.ReferencePath;

    /// <summary>The extension that carries the scope, so the page is also the scope reference (FR-A-18).</summary>
    public const string ScopeProperty = "x-required-scope";

    /// <summary>What the two key-free health routes declare instead of a scope.</summary>
    public const string NoScope = "none";

    /// <summary>Declared because FR-A-18 asks for it; not what this build accepts (Q-67).</summary>
    public const string BearerScheme = "bearerAuth";

    /// <summary>The scheme the document offers, because it is the one <c>ApiKeyMiddleware</c> reads.</summary>
    public const string ApiKeyScheme = "apiKeyAuth";
}
