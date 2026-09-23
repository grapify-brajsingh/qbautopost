using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// T-914 (api-v1 §9.2, §9.4): the documents and scripts an operator actually follows, checked against the surface
/// the application actually serves.
/// <para>
/// Nine sessions of M9 moved every route to <c>/api/v1</c> and narrowed the health routes to per-caller keys and
/// scopes, while the runbook, the POC package and the deploy scripts went on describing the flat paths and one
/// shared key. Nobody notices a document drifting; a build does. These are the two claims that would silently
/// become false again: that the operator instructions call the versioned routes, and that they never tell anyone a
/// protected route needs no key.
/// </para>
/// <para>
/// The flat paths still answer while <c>Api:LegacyRoutes</c> is true (api-v1 §9.1) — this is not about breaking
/// them, it is about the documentation naming the surface that will still be there after they are switched off
/// (Q-53).
/// </para>
/// </summary>
public sealed class OperatorDocsTests
{
    /// <summary>The documents and scripts that tell a person or a script how to call this API.</summary>
    public static TheoryData<string> OperatorFiles => new()
    {
        "docs/runbook.md",
        "steps.md",
        "samples/poc/README-POC.md",
        "scripts/qb-server-check.ps1",
        "deploy/start-all.ps1",
    };

    /// <summary>A route of spec §6 / api-v1 §3, as it appears in text when somebody writes down a URL.</summary>
    private static readonly string[] Routes =
        ["/health", "/jobs", "/batches/", "/rules/", "/quickbooks/", "/qb/sync-lists"];

    private const string Prefix = "/api/v1";

    [Theory]
    [MemberData(nameof(OperatorFiles))]
    public void Should_NameTheVersionedRoutes_When_ReadingOperatorInstructions(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.Find(), file));

        Assert.Empty(FlatRouteUses(text).Select(use => $"{file}: {use}"));
    }

    /// <summary>
    /// Session 15 narrowed api-v1 §2.2: only <c>/health</c> and <c>/health/ready</c> answer without a key, and
    /// <c>/health/sdk</c>, <c>/health/quickbooks</c> and <c>/health/hermes</c> need <c>health:read</c>. Two files
    /// still promised the operator the old rule, which is a 401 in the middle of a server checklist.
    /// </summary>
    [Theory]
    [MemberData(nameof(OperatorFiles))]
    public void Should_NeverPromiseAKeyFreeHealthCheck_When_ReadingOperatorInstructions(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.Find(), file));

        Assert.DoesNotContain("no key needed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No API key needed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("needs no key for /health", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The server checklist calls a route that now needs <c>health:read</c>, so it must send the key.</summary>
    [Fact]
    public void Should_SendTheKeyToQuickBooksHealth_When_ReadingTheServerCheck()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find(), "scripts", "qb-server-check.ps1"));

        Assert.Contains("Invoke-Api GET '/api/v1/health/quickbooks'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>start-all.ps1</c> runs at logon and must never hold a secret (see
    /// <see cref="DeployScriptsTests.Should_NotTouchSecrets_When_ReadingDeployScript"/>), so the only route it may
    /// wait on is one that needs no key at all: readiness.
    /// </summary>
    [Fact]
    public void Should_WaitOnAKeyFreeRoute_When_ReadingStartAll()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find(), "deploy", "start-all.ps1"));

        Assert.Contains("/api/v1/health/ready", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator surface M9 added. Each of these is something the owner has to do or read on the server and
    /// cannot find out from the code: how a key is issued and what a scope allows, when the shared key may be
    /// turned off, where the audit file is, how to read the API reference, what a 429 means, and which folders
    /// jobs may come from.
    /// </summary>
    [Theory]
    [InlineData("new-api-client.ps1")]
    [InlineData("Api:AllowLegacyKey")]
    [InlineData("audit-")]
    [InlineData("Api:Reference:Enabled")]
    [InlineData("Retry-After")]
    [InlineData("Paths:AllowedJobRoots")]
    [InlineData("qb:post")]
    public void Should_DocumentTheM9OperatorSurface_When_ReadingTheRunbook(string subject)
    {
        var runbook = File.ReadAllText(Path.Combine(RepoRoot.Find(), "docs", "runbook.md"));

        Assert.Contains(subject, runbook, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every place a route is named without the version prefix in front of it. Only whole route words count: a
    /// Windows path such as <c>C:\qb-jobs</c> has no <c>/jobs</c> in it, and <c>rules.json</c> is not <c>/rules/</c>.
    /// </summary>
    private static IEnumerable<string> FlatRouteUses(string text)
    {
        foreach (var route in Routes)
        {
            for (var i = text.IndexOf(route, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(route, i + 1, StringComparison.Ordinal))
            {
                var before = text[..i];
                if (!before.EndsWith(Prefix, StringComparison.Ordinal))
                {
                    yield return $"line {text.Take(i).Count(c => c == '\n') + 1}: {route}";
                }
            }
        }
    }
}
