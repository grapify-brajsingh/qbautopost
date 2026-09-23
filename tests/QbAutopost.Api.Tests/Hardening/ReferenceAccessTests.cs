using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-918 / FR-A-18, answering <b>Q-66</b>: the reference and the document need no key in Development, and
/// <c>health:read</c> everywhere else.
/// <para>
/// T-913 deliberately took the stricter reading — <c>health:read</c> in every environment — because rule 4 asks for
/// the answer that reveals less when a spec allows two. Q-66 recorded the cost it bought: the key travels in a
/// header, so <b>the page cannot be opened in a plain browser at all</b>, which makes a documentation UI useless for
/// the one thing a documentation UI is for. Running the host confirmed it: navigating to <c>/api/v1/reference</c>
/// returns a 401 problem document, because a browser cannot attach <c>X-Api-Key</c>.
/// </para>
/// <para>
/// The owner answered Q-66 by choosing the spec's literal rule. Development only — and Development is the
/// developer's own machine, where <c>TransportGuard</c> still refuses a non-loopback bind without TLS.
/// </para>
/// </summary>
public sealed class ReferenceAccessTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public static TheoryData<string> ReferenceRoutes() =>
    [
        ApiRoutes.V1Prefix + ApiRoutes.OpenApiPath,
        ApiRoutes.V1Prefix + ApiRoutes.ReferencePath,
    ];

    [Theory]
    [MemberData(nameof(ReferenceRoutes))]
    public async Task Should_ServeWithoutAKey_When_TheEnvironmentIsDevelopment(string route)
    {
        using var host = Host("Development");
        using var client = host.CreateClient(NoRedirects);

        using var response = await client.GetAsync(route);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ReferenceRoutes))]
    public async Task Should_Refuse_When_TheEnvironmentIsNotDevelopmentAndNoKeyIsSent(string route)
    {
        using var host = Host("Staging");
        using var client = host.CreateClient(NoRedirects);

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ReferenceRoutes))]
    public async Task Should_Serve_When_TheKeyIsSentOutsideDevelopment(string route)
    {
        using var host = Host("Staging");
        using var client = host.CreateClient(NoRedirects);
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.GetAsync(route);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Should_KeepTheStrictRule_When_NoEnvironmentIsGiven()
    {
        // The default is the Production rule, so the OpenAPI drift test keeps asserting the contract the document
        // declares, and any future caller of ScopeFor is strict unless it says otherwise.
        Assert.Equal(
            ApiScopes.HealthRead,
            ApiRoutes.ScopeFor("GET", ApiRoutes.V1Prefix + ApiRoutes.ReferencePath));
    }

    [Fact]
    public void Should_OpenTheDocumentation_When_DevelopmentIsStatedExplicitly() =>
        Assert.Null(ApiRoutes.ScopeFor("GET", ApiRoutes.V1Prefix + ApiRoutes.ReferencePath, isDevelopment: true));

    [Fact]
    public void Should_LeaveEveryOtherRouteAlone_When_TheEnvironmentIsDevelopment()
    {
        // Development opens the documentation and nothing else. A money route stays shut in every environment.
        Assert.Equal(
            ApiScopes.QbPost,
            ApiRoutes.ScopeFor("POST", ApiRoutes.V1Prefix + "/quickbooks/transactions", isDevelopment: true));
        Assert.Equal(
            ApiScopes.JobsRead,
            ApiRoutes.ScopeFor("GET", ApiRoutes.V1Prefix + "/jobs", isDevelopment: true));
    }

    private static WebApplicationFactoryClientOptions NoRedirects => new() { AllowAutoRedirect = false };

    private WebApplicationFactory<Program> Host(string environment) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("Api:Reference:Enabled", "true");
        });

    public void Dispose() => _factory.Dispose();
}
