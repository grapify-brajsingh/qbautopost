using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// T-1602 for <c>deploy/deploy.ps1</c>. Static checks only: the real release is verified on the server.
/// Order checks read what follows the <c># Main flow</c> marker, because the functions above it name the same commands.
/// </summary>
public sealed class WindowsDeployScriptTests
{
    private const string MainFlowMarker = "# Main flow";

    private static string Read() =>
        File.ReadAllText(Path.Combine(RepoRoot.Find(), "deploy", "deploy.ps1"));

    private static string MainFlow()
    {
        var script = Read();
        var marker = script.IndexOf(MainFlowMarker, StringComparison.Ordinal);
        Assert.True(marker >= 0, "deploy.ps1 needs a '# Main flow' line before the main body");
        return script[(marker + MainFlowMarker.Length)..];
    }

    private static string FunctionBody(string name)
    {
        var script = Read();
        var start = script.IndexOf("function " + name, StringComparison.Ordinal);
        Assert.True(start >= 0, $"function {name} not found");
        var next = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var marker = script.IndexOf(MainFlowMarker, start, StringComparison.Ordinal);
        var end = next >= 0 && next < marker ? next : marker;
        return script[start..end];
    }

    private static void AssertInOrder(string text, params string[] tokens)
    {
        var position = -1;
        foreach (var token in tokens)
        {
            var found = text.IndexOf(token, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{token}' not found");
            Assert.True(found > position, $"'{token}' comes too early");
            position = found;
        }
    }

    [Fact]
    public void Should_SupportWhatIfAndBeAsciiOnly_When_ReadingDeploy()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot.Find(), "deploy", "deploy.ps1"));

        Assert.Contains("SupportsShouldProcess", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.All(bytes, b => Assert.True(b < 0x80));
    }

    [Fact]
    public void Should_PullFastForwardOnly_When_ReadingDeploy()
    {
        Assert.Contains("--ff-only", Read(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_BuildBeforeStoppingTheApi_When_ReadingDeploy()
    {
        Assert.Contains("Stop-Process", FunctionBody("Stop-Api"), StringComparison.Ordinal);
        AssertInOrder(MainFlow(), "npm.cmd", "dotnet", "publish", "Stop-Api");
    }

    [Fact]
    public void Should_BackUpBeforeMigrating_When_ReadingDeploy()
    {
        AssertInOrder(MainFlow(), "Stop-Api", "backup-postgres.ps1", "apply-migrations.ps1", "-NewName 'app.old'");
    }

    [Fact]
    public void Should_StartThroughTheScheduledTask_When_ReadingDeploy()
    {
        var script = Read();

        Assert.Contains("Start-ScheduledTask", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseTheWrongAccount_When_ReadingDeploy()
    {
        var preflight = FunctionBody("Test-Preflight");

        Assert.Contains("Principal.UserId", preflight, StringComparison.Ordinal);
        Assert.Contains("Refused", Read(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CheckTheTaskExists_When_ReadingDeploy()
    {
        Assert.Contains("Get-ScheduledTask", FunctionBody("Test-Preflight"), StringComparison.Ordinal);
        AssertInOrder(MainFlow(), "Test-Preflight", "Stop-Api");
    }

    [Fact]
    public void Should_RestoreTheOldVersion_When_TheSwapOrStartFails()
    {
        var main = MainFlow();
        var swap = main.IndexOf("-NewName 'app.old'", StringComparison.Ordinal);
        Assert.True(swap >= 0, "the swap belongs to the main flow");
        var afterSwap = main[swap..];

        // The swap's own catch, then the failed readiness path after it.
        var swapCatch = afterSwap.IndexOf("catch", StringComparison.Ordinal);
        var first = afterSwap.IndexOf("Restore-Previous", StringComparison.Ordinal);
        var ready = afterSwap.IndexOf("Wait-Ready", first + 1, StringComparison.Ordinal);
        var second = afterSwap.IndexOf("Restore-Previous", ready + 1, StringComparison.Ordinal);
        Assert.True(swapCatch >= 0 && swapCatch < first, "the swap's catch must call Restore-Previous");
        Assert.True(first < ready && ready < second, "Restore-Previous must also follow the readiness wait");
    }

    [Fact]
    public void Should_HandleAFirstDeploy_When_ReadingDeploy()
    {
        var main = MainFlow();
        var restore = FunctionBody("Restore-Previous");

        AssertInOrder(main, "Test-Path -LiteralPath $appLive", "-NewName 'app.old'");
        Assert.Contains("Test-Path -LiteralPath $appOld", restore, StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath $dashboardOld", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RollBackOnlyWhatThisRunReplaced_When_ReadingDeploy()
    {
        // A dashboard.old left by an earlier release must not replace a dashboard this run never touched.
        var restore = FunctionBody("Restore-Previous");

        Assert.Contains("$script:DashboardInstalled", restore, StringComparison.Ordinal);
        Assert.Contains("$script:AppMoved", restore, StringComparison.Ordinal);
        Assert.Contains("$script:DashboardInstalled = $true", MainFlow(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ClearAStillRunningTask_When_StartingTheApi()
    {
        // The task ignores a second start while the first still runs, so a restore would silently start nothing.
        AssertInOrder(FunctionBody("Start-Api"), "Stop-ScheduledTask", "Start-ScheduledTask");
    }

    [Fact]
    public void Should_KeepAPreDeployDumpBeforeMigrating_When_ReadingDeploy()
    {
        // A same-day retry overwrites qbautopost-yyyyMMdd.dump, and only that name is under the retention glob.
        var save = FunctionBody("Save-PreDeployDump");

        Assert.Contains("'pre-deploy-{0}-{1}.dump'", save, StringComparison.Ordinal);
        Assert.Contains("yyyyMMdd-HHmmss", save, StringComparison.Ordinal);
        Assert.Contains("$script:OldCommit", save, StringComparison.Ordinal);
        AssertInOrder(MainFlow(), "backup-postgres.ps1", "Save-PreDeployDump", "apply-migrations.ps1");
        Assert.Contains("$script:PreDeployDump", FunctionBody("Write-MigrationWarning"), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_WarnAboutTheSchema_When_TheMigrationStepFails()
    {
        // apply-migrations.ps1 can fail after migrations are committed. A failed backup changed nothing and gets no warning.
        AssertInOrder(
            MainFlow(),
            "$script:MigrationStarted = $true",
            "apply-migrations.ps1",
            "if ($script:MigrationStarted) { Write-MigrationWarning }",
            "Start-Unchanged");
    }

    [Fact]
    public void Should_EndWithTheOutcome_When_RestoringThePreviousVersion()
    {
        var main = MainFlow();
        var outcome = FunctionBody("Write-RestoreResult");

        Assert.DoesNotContain("[void] (Restore-Previous)", main, StringComparison.Ordinal);
        var first = main.IndexOf("Write-RestoreResult (Restore-Previous)", StringComparison.Ordinal);
        var last = main.LastIndexOf("Write-RestoreResult (Restore-Previous)", StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first, "both restore paths must report the outcome");
        Assert.Contains("is running and ready", outcome, StringComparison.Ordinal);
        Assert.Contains("Treat the API as down", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseADisabledTask_When_ReadingDeploy()
    {
        Assert.Contains("$task.State -eq 'Disabled'", FunctionBody("Test-Preflight"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DryRunDefault")]
    [InlineData("New-NetFirewallRule")]
    [InlineData("SetEnvironmentVariable")]
    [InlineData("ledger.json")]
    [InlineData("rules.json")]
    [InlineData("Register-ScheduledTask")]
    public void Should_NotTouchServerState_When_ReadingDeploy(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Read(), StringComparison.OrdinalIgnoreCase);
    }
}
