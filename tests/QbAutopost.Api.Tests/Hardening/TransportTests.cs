using System.Net;
using Microsoft.Extensions.Configuration;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-911 / FR-A-14: transport. Until D-4 the app was safe because nothing off the server could reach it; once callers
/// are remote, "it works on loopback" stops being an argument.
/// <para>
/// The shape of the guarantee: a bind that strangers can reach <b>refuses to start</b> without TLS, unless an
/// operator has said <c>Api:AllowInsecureRemote=true</c> out loud — and then it says so at startup and on every
/// request, so nobody discovers it a year later. A CORS wildcard is refused outright while any route needs a key.
/// </para>
/// <para>
/// The namespace is <c>Hardening</c>, not <c>Security</c>, on purpose: a test namespace that shadows the production
/// one broke four files at T-901 (handoff trap 2).
/// </para>
/// </summary>
public sealed class TransportTests
{
    private const string Loopback = "http://127.0.0.1:5080";
    private const string Remote = "http://0.0.0.0:5080";
    private const string RemoteTls = "https://0.0.0.0:5443";

    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://[::1]:5080")]
    public void Should_Start_When_BindIsLoopbackWithoutTls(string bind)
    {
        var decision = TransportGuard.Inspect(Api(bind), Paths(), pfxPasswordFromFile: false);

        Assert.Null(decision.Refusal);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://+:5080")]
    [InlineData("http://10.0.0.5:5080")]
    public void Should_Refuse_When_BindIsReachableRemotelyWithoutTls(string bind)
    {
        var decision = TransportGuard.Inspect(Api(bind), Paths("/srv/jobs"), pfxPasswordFromFile: false);

        Assert.NotNull(decision.Refusal);
        Assert.Contains("Api:AllowInsecureRemote", decision.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Refuse_When_OneOfSeveralBindsIsRemoteWithoutTls()
    {
        var decision = TransportGuard.Inspect(
            Api("http://127.0.0.1:5080;http://0.0.0.0:5081"), Paths("/srv/jobs"), pfxPasswordFromFile: false);

        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void Should_Start_When_RemoteBindUsesTls()
    {
        var api = Api(RemoteTls);
        api.Tls.PfxPath = "server.pfx";

        var decision = TransportGuard.Inspect(api, Paths("/srv/jobs"), pfxPasswordFromFile: false);

        Assert.Null(decision.Refusal);
    }

    [Fact]
    public void Should_Start_When_InsecureRemoteIsAllowedExplicitly()
    {
        var api = Api(Remote);
        api.AllowInsecureRemote = true;

        var decision = TransportGuard.Inspect(api, Paths("/srv/jobs"), pfxPasswordFromFile: false);

        Assert.Null(decision.Refusal);
        Assert.Contains(decision.Warnings, w => w.Contains("AllowInsecureRemote", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Refuse_When_CorsAllowsEveryOrigin()
    {
        var api = Api(Loopback);
        api.Cors.AllowedOrigins = ["*"];

        var decision = TransportGuard.Inspect(api, Paths(), pfxPasswordFromFile: false);

        Assert.NotNull(decision.Refusal);
        Assert.Contains("*", decision.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Start_When_CorsNamesSpecificOrigins()
    {
        var api = Api(Loopback);
        api.Cors.AllowedOrigins = ["https://erp.example.com"];

        var decision = TransportGuard.Inspect(api, Paths(), pfxPasswordFromFile: false);

        Assert.Null(decision.Refusal);
    }

    /// <summary>
    /// SPEC-GAP T-911: FR-A-17 requires caller paths to sit inside <c>Paths:AllowedJobRoots</c>, and §12 ships that
    /// list empty. An empty list cannot mean "refuse every job" (no installation would start) and must not quietly
    /// mean "any absolute path" once strangers can call. It means unrestricted only while the bind is loopback.
    /// </summary>
    [Fact]
    public void Should_Refuse_When_RemoteBindHasNoJobRootAllowList()
    {
        var api = Api(RemoteTls);
        api.Tls.PfxPath = "server.pfx";

        var decision = TransportGuard.Inspect(api, Paths(), pfxPasswordFromFile: false);

        Assert.NotNull(decision.Refusal);
        Assert.Contains("Paths:AllowedJobRoots", decision.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Warn_When_PfxPasswordCameFromTheSettingsFile()
    {
        var api = Api(RemoteTls);
        api.Tls.PfxPath = "server.pfx";
        api.Tls.PfxPassword = "hunter2";

        var decision = TransportGuard.Inspect(api, Paths("/srv/jobs"), pfxPasswordFromFile: true);

        // Accepted, not refused: refusing would strand an operator mid-deploy (api-v1 §12).
        Assert.Null(decision.Refusal);
        var warning = Assert.Single(decision.Warnings, w => w.Contains("PfxPassword", StringComparison.Ordinal));
        Assert.Contains("appsettings", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NotWarn_When_PfxPasswordCameFromTheEnvironment()
    {
        var api = Api(RemoteTls);
        api.Tls.PfxPath = "server.pfx";
        api.Tls.PfxPassword = "hunter2";

        var decision = TransportGuard.Inspect(api, Paths("/srv/jobs"), pfxPasswordFromFile: false);

        Assert.DoesNotContain(decision.Warnings, w => w.Contains("PfxPassword", StringComparison.Ordinal));
    }

    /// <summary>
    /// The warning above is only worth anything if "came from a file" is detected correctly, so the detector is
    /// tested against a real JSON file rather than a flag a caller passes in.
    /// </summary>
    [Fact]
    public void Should_SeeAFileValue_When_ASettingsFileSuppliedThePfxPassword()
    {
        using var dir = new TempDir();
        var file = dir.Combine("appsettings.json");
        File.WriteAllText(file, """{ "Api": { "Tls": { "PfxPassword": "from-json" } } }""");
        var config = new ConfigurationBuilder().AddJsonFile(file).Build();

        Assert.True(TransportGuard.CameFromFile(config, "Api:Tls:PfxPassword"));
    }

    [Fact]
    public void Should_NotSeeAFileValue_When_OnlyTheEnvironmentSuppliedThePfxPassword()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:Tls:PfxPassword"] = "from-env" })
            .Build();

        Assert.False(TransportGuard.CameFromFile(config, "Api:Tls:PfxPassword"));
    }

    [Fact]
    public void Should_EnableHsts_When_TlsIsConfigured()
    {
        var api = Api(RemoteTls);
        api.Tls.PfxPath = "server.pfx";

        Assert.True(TransportGuard.UseHsts(api));
        Assert.False(TransportGuard.UseHsts(Api(Loopback)));
    }

    [Fact]
    public void Should_Throw_When_RequireIsCalledOnARefusedConfiguration()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TransportGuard.Require(Api(Remote), Paths("/srv/jobs"), pfxPasswordFromFile: false));

        Assert.Contains("Api:AllowInsecureRemote", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SendHardeningHeaders_When_AnyRouteAnswers()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Should_SendHardeningHeaders_When_TheRequestIsRefused()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/hermes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    private static ApiSettings Api(string bind) => new() { Bind = bind };

    private static PathsSettings Paths(params string[] jobRoots) => new() { AllowedJobRoots = [.. jobRoots] };
}
