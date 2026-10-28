using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// T-1117 for <c>deploy/install-dashboard.ps1</c>, the runbook and the checklist. Static checks only: the real install
/// is verified on the server (docs/testing/T-1117.server-checklist.md).
/// </summary>
public sealed class DashboardDeployScriptTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot.Find() }.Concat(parts).ToArray()));

    [Fact]
    public void Should_RenameAtomically_When_InstallingDashboard()
    {
        var script = Read("deploy", "install-dashboard.ps1");

        Assert.Contains("dashboard.new", script, StringComparison.Ordinal);
        Assert.Contains("Rename-Item", script, StringComparison.Ordinal);
        var copy = script.IndexOf("Copy-Item", StringComparison.Ordinal);
        var rename = script.IndexOf("Rename-Item -LiteralPath $newDir", StringComparison.Ordinal);
        Assert.True(copy > 0 && rename > copy, "copy to the .new folder must come before the final rename");
    }

    [Fact]
    public void Should_KeepPreviousVersionAsOld_When_InstallingDashboard()
    {
        var script = Read("deploy", "install-dashboard.ps1");

        Assert.Contains("'.old'", script, StringComparison.Ordinal);
        Assert.Contains("-Rollback", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_SupportWhatIfAndBeAsciiOnly_When_ReadingInstallDashboard()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot.Find(), "deploy", "install-dashboard.ps1"));

        Assert.Contains("SupportsShouldProcess", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.All(bytes, b => Assert.True(b < 0x80));
    }

    [Theory]
    [InlineData("New-NetFirewallRule")]
    [InlineData("New-Service")]
    [InlineData("Register-ScheduledTask")]
    [InlineData("Import-Certificate")]
    [InlineData("ApiKey")]
    public void Should_ChangeOnlyDashboardFolders_When_ReadingInstallDashboard(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Read("deploy", "install-dashboard.ps1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_DocumentHttpsFirewallAndProxyTimeout_When_ReadingRunbook()
    {
        var runbook = Read("docs", "runbook.md");

        Assert.Contains("https://0.0.0.0:5443", runbook, StringComparison.Ordinal);
        Assert.Contains("RemoteAddress LocalSubnet", runbook, StringComparison.Ordinal);
        Assert.Contains("10 minutes", runbook, StringComparison.Ordinal);
        Assert.Contains("install-dashboard.ps1", runbook, StringComparison.Ordinal);
        Assert.Contains("dashboard.old", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ListTheServerChecks_When_ReadingChecklist()
    {
        var list = Read("docs", "testing", "T-1117.server-checklist.md");

        Assert.Contains("(post|pushId|email)", list, StringComparison.Ordinal);
        Assert.Contains("DashboardV2", list, StringComparison.Ordinal);
        Assert.Contains("AuditEventsView", list, StringComparison.Ordinal);
        Assert.Contains("Q-96", list, StringComparison.Ordinal);
        Assert.Contains("GET /jobs", list, StringComparison.Ordinal);
    }
}
