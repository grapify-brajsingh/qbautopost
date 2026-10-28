using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// T-1203 / ADR-0011: the model calls go to OpenRouter over HTTPS, so the deploy scripts no longer start Docker or
/// Hermes. The API start and its readiness wait stay.
/// </summary>
public sealed class OpenRouterDeployScriptTests
{
    private static string Script(string file) => File.ReadAllText(Path.Combine(RepoRoot.Find(), "deploy", file));

    [Fact]
    public void Should_NotReferenceDocker_When_StartAllRead()
    {
        var script = Script("start-all.ps1");

        Assert.DoesNotContain("docker", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start-Api -Exe", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ApiReady", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_NotReferenceDockerOrHermes_When_InstallTaskRead()
    {
        var script = Script("install-task.ps1");

        Assert.DoesNotContain("docker", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hermes", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_NotDeclareDockerParameters_When_StartAllRead()
    {
        var script = Script("start-all.ps1");

        Assert.DoesNotContain("-DockerMode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("WslDistro", script, StringComparison.Ordinal);
        Assert.DoesNotContain("hermes", script, StringComparison.OrdinalIgnoreCase);
    }
}
