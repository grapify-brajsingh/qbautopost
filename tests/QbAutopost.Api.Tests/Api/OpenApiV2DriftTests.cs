using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Models;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-1109 (dashboard v2 spec 15): <c>wwwroot/openapi-v2.json</c> against the <c>/api/v2</c> routes, with the checks
/// <see cref="OpenApiDriftTests"/> applies to the v1 document: every v2 route documented, every operation mapped,
/// each with a summary, a success schema and the scope <see cref="ApiRoutes.ScopeFor"/> enforces, and every enum
/// listing what the serialiser accepts. The v1 document is not read here; it covers <c>/api/v1</c> only.
/// </summary>
public sealed class OpenApiV2DriftTests : IDisposable
{
    private const string DocumentRoute = ApiRoutes.V2Prefix + ApiRoutes.OpenApiPath;

    private readonly ApiFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public OpenApiV2DriftTests() => _host = _factory.WithSetting("Api:Reference:Enabled", "true");

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_DocumentEveryV2Route_When_DriftChecked()
    {
        using var document = await DocumentAsync();

        var undocumented = MappedRoutes()
            .Where(route => !Operations(document).Any(o => o.Method == route.Method && o.Path == route.Path))
            .Select(route => $"{route.Method} {route.Path}")
            .ToList();

        Assert.Empty(undocumented);
    }

    [Fact]
    public async Task Should_MapEveryDocumentedOperation_When_DriftChecked()
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
    public async Task Should_GiveEveryOperationASummary_When_TheV2DocumentIsRead()
    {
        using var document = await DocumentAsync();

        var missing = Operations(document)
            .Where(o => !o.Value.TryGetProperty("summary", out var summary) || string.IsNullOrWhiteSpace(summary.GetString()))
            .Select(o => $"{o.Method} {o.Path}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Should_GiveEveryOperationASuccessResponseSchema_When_TheV2DocumentIsRead()
    {
        using var document = await DocumentAsync();

        var missing = Operations(document).Where(o => !HasSuccessSchema(o.Value)).Select(o => $"{o.Method} {o.Path}").ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Should_DeclareTheScopeTheMiddlewareEnforces_When_TheV2DocumentIsRead()
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
    public async Task Should_DeclareNoSecurityOnlyOnOpenRoutes_When_TheV2DocumentIsRead()
    {
        using var document = await DocumentAsync();

        var wrong = new List<string>();
        foreach (var operation in Operations(document))
        {
            var open = ApiRoutes.ScopeFor(operation.Method, operation.Path) is null;
            var declaresNoSecurity = operation.Value.TryGetProperty("security", out var security) && security.GetArrayLength() == 0;
            if (open != declaresNoSecurity)
            {
                wrong.Add($"{operation.Method} {operation.Path}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task Should_ListTheValuesTheSerialiserAccepts_When_TheV2DocumentNamesAnEnum()
    {
        using var document = await DocumentAsync();
        var enums = typeof(TxnKind).Assembly.GetTypes()
            .Where(type => type.IsEnum)
            .GroupBy(type => type.Name)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

        var wrong = new List<string>();
        var compared = new List<string>();
        foreach (var schema in Schemas(document).EnumerateObject())
        {
            if (!enums.TryGetValue(schema.Name, out var type) || !schema.Value.TryGetProperty("enum", out var values))
            {
                continue;
            }

            compared.Add(schema.Name);
            var documented = values.EnumerateArray().Select(value => value.GetString()).OrderBy(v => v, StringComparer.Ordinal);
            var accepted = Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName).OrderBy(v => v, StringComparer.Ordinal);
            if (!documented.SequenceEqual(accepted, StringComparer.Ordinal))
            {
                wrong.Add($"{schema.Name}: documented [{string.Join(", ", documented)}], accepted [{string.Join(", ", accepted)}]");
            }
        }

        Assert.Empty(wrong);
        Assert.Contains(nameof(TxnKind), compared);
    }

    /// <summary>Spec 10 item 4.7: the v2 document says <c>skip</c> is refused on input and <c>post: false</c> skips a row.</summary>
    [Fact]
    public async Task Should_SayHowToSkipARow_When_TxnKindIsDocumented()
    {
        using var document = await DocumentAsync();

        var description = Schemas(document).GetProperty(nameof(TxnKind)).GetProperty("description").GetString() ?? string.Empty;

        Assert.Contains("`skip`", description, StringComparison.Ordinal);
        Assert.Contains("refused on input", description, StringComparison.Ordinal);
        Assert.Contains("`post: false`", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferTheApiKeySchemeAndDeclareBearer_When_TheV2DocumentIsRead()
    {
        using var document = await DocumentAsync();

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        var offered = document.RootElement.GetProperty("security")[0].EnumerateObject().Single().Name;

        Assert.Equal(OpenApi.ApiKeyScheme, offered);
        Assert.Equal(ApiSettings.KeyHeader, schemes.GetProperty(offered).GetProperty("name").GetString());
        Assert.Equal("bearer", schemes.GetProperty(OpenApi.BearerScheme).GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task Should_ServeTheV2Document_When_ReferenceIsEnabled()
    {
        using var client = WithKey(_host, ApiFactory.ApiKey);

        using var response = await client.GetAsync(DocumentRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2.0", document.RootElement.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Should_NotServeTheV2Document_When_ReferenceIsDisabled()
    {
        using var host = _factory.WithSetting("Api:Reference:Enabled", "false");
        using var client = WithKey(host, ApiFactory.ApiKey);

        using var response = await client.GetAsync(DocumentRoute);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseTheV2Document_When_TheCallerLacksHealthRead()
    {
        using var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Users.TokenFor(DashboardRoles.Admin));

        using var response = await client.GetAsync(DocumentRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<JsonDocument> DocumentAsync()
    {
        using var client = WithKey(_host, ApiFactory.ApiKey);
        using var response = await client.GetAsync(DocumentRoute);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Every <c>/api/v2</c> route the host actually mapped.</summary>
    private IEnumerable<(string Method, string Path)> MappedRoutes()
    {
        var source = _host.Services.GetRequiredService<EndpointDataSource>();
        var routes = new HashSet<(string, string)>();
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');
            if (!path.StartsWith(ApiRoutes.V2Prefix + "/", StringComparison.OrdinalIgnoreCase))
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

    private static JsonElement Schemas(JsonDocument document) =>
        document.RootElement.GetProperty("components").GetProperty("schemas");

    private static readonly HashSet<string> Methods =
        new(StringComparer.Ordinal) { "get", "put", "post", "delete", "patch", "head", "options", "trace" };

    private static IEnumerable<(string Path, string Method, JsonElement Value)> Operations(JsonDocument document)
    {
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
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
            if (response.Name.StartsWith('2')
                && response.Value.TryGetProperty("content", out var content)
                && content.EnumerateObject().Any(media => media.Value.TryGetProperty("schema", out _)))
            {
                return true;
            }
        }

        return false;
    }

    private static HttpClient WithKey(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiSettings.KeyHeader, key);
        return client;
    }
}
