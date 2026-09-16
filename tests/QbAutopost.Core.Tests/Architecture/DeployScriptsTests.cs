using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// Spec §4 for <c>deploy/start-all.ps1</c> and <c>deploy/install-task.ps1</c> (T-802): an interactive logon task, never a
/// service; Hermes checked before the API starts; a dry run (-WhatIf); ASCII only because Windows PowerShell 5.1 reads
/// a BOM-less script as ANSI. The real run is on the server (tracker T-802 checklist).
/// </summary>
public sealed class DeployScriptsTests
{
    private static readonly string DeployDir = Path.Combine(RepoRoot.Find(), "deploy");

    [Theory]
    [InlineData("deploy/start-all.ps1")]
    [InlineData("deploy/install-task.ps1")]
    [InlineData("scripts/shadow-diff.ps1")]
    public void Should_BeAsciiOnly_When_ReadingOperatorScript(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot.Find(), file));

        Assert.All(bytes, b => Assert.True(b < 0x80, $"{file} contains a non-ASCII byte 0x{b:X2}"));
    }

    [Theory]
    [InlineData("start-all.ps1")]
    [InlineData("install-task.ps1")]
    public void Should_SupportWhatIf_When_ReadingDeployScript(string file)
    {
        Assert.Contains("SupportsShouldProcess", Script(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("start-all.ps1")]
    [InlineData("install-task.ps1")]
    public void Should_NotTouchSecrets_When_ReadingDeployScript(string file)
    {
        var script = Script(file);

        Assert.DoesNotContain("ApiKey", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API_SERVER_KEY", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Password", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_RegisterInteractiveLogonTask_When_ReadingInstallTask()
    {
        var script = Script("install-task.ps1");

        Assert.Contains("-AtLogOn", script, StringComparison.Ordinal);
        Assert.Contains("-LogonType Interactive", script, StringComparison.Ordinal);
        Assert.Contains("start-all.ps1", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("New-Service")]
    [InlineData("sc.exe")]
    [InlineData("S4U")]
    [InlineData("ServiceAccount")]
    public void Should_NeverInstallAService_When_ReadingInstallTask(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Script("install-task.ps1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_CheckHermesBeforeStartingApi_When_ReadingStartAll()
    {
        var script = Script("start-all.ps1");
        var waitHermes = script.IndexOf("Wait-HermesPort -", StringComparison.Ordinal);
        var startApi = script.IndexOf("Start-Api -", StringComparison.Ordinal);

        Assert.True(waitHermes > 0, "start-all.ps1 must call Wait-HermesPort");
        Assert.True(startApi > waitHermes, "start-all.ps1 must wait for Hermes before it starts the API");
        Assert.Contains("/health/hermes", script, StringComparison.Ordinal);
        Assert.Contains("docker compose", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_UseLoopbackOnly_When_ReadingStartAll()
    {
        var script = Script("start-all.ps1");

        Assert.Contains("127.0.0.1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Invoke-RestMethod")]
    [InlineData("Invoke-WebRequest")]
    [InlineData("ApiKey")]
    public void Should_StayOffline_When_ReadingShadowDiff(string forbidden)
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find(), "scripts", "shadow-diff.ps1"));

        Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
    }

    private static string Script(string file) => File.ReadAllText(Path.Combine(DeployDir, file));
}
