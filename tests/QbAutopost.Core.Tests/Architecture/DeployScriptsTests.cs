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
    [InlineData("scripts/new-api-client.ps1")]
    [InlineData("scripts/qb-server-check.ps1")]
    [InlineData("deploy/install-postgres.ps1")]
    [InlineData("deploy/apply-migrations.ps1")]
    [InlineData("deploy/backup-postgres.ps1")]
    [InlineData("deploy/pg-common.ps1")]
    [InlineData("deploy/grants.sql")]
    public void Should_BeAsciiOnly_When_ReadingOperatorScript(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot.Find(), file));

        Assert.All(bytes, b => Assert.True(b < 0x80, $"{file} contains a non-ASCII byte 0x{b:X2}"));
    }

    [Theory]
    [InlineData("start-all.ps1")]
    [InlineData("install-task.ps1")]
    [InlineData("install-postgres.ps1")]
    [InlineData("apply-migrations.ps1")]
    [InlineData("backup-postgres.ps1")]
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
        // T-1203 / ADR-0011: no Hermes or Docker step is left; the model provider is checked through the API
        // (health:read route), not by the script.
        var script = Script("start-all.ps1");
        var startApi = script.IndexOf("Start-Api -", StringComparison.Ordinal);

        Assert.True(startApi > 0, "start-all.ps1 must start the API");
        Assert.DoesNotContain("Wait-HermesPort", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose", script, StringComparison.Ordinal);
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

    /// <summary>
    /// T-1016 / CLAUDE.md rule 6: a generated password reaches machine environment variables and a child process's
    /// PGPASSWORD, never a command line, a file or a committed default. Windows PowerShell 5.1 only: no 7-only operators.
    /// </summary>
    [Theory]
    [InlineData("install-postgres.ps1")]
    [InlineData("apply-migrations.ps1")]
    [InlineData("backup-postgres.ps1")]
    [InlineData("pg-common.ps1")]
    public void Should_KeepPasswordsOffCommandLinesAndOutOfFiles_When_ReadingPostgresScript(string file)
    {
        var script = Script(file);

        Assert.DoesNotContain("--password", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--superpassword", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PGPASSWORD=", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("postgres://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-W ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" ?? ", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" && ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-AsPlainText", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_BindToLoopbackAndWriteMachineVariables_When_ReadingInstallPostgres()
    {
        var script = Script("install-postgres.ps1");

        Assert.Contains("listen_addresses = '127.0.0.1'", script, StringComparison.Ordinal);
        Assert.Contains("'Machine'", script, StringComparison.Ordinal);
        Assert.Contains("QBAUTOPOST__Database__ConnectionString", script, StringComparison.Ordinal);
        Assert.Contains("QBAUTOPOST_MIGRATION_CONNECTION", script, StringComparison.Ordinal);
        Assert.Contains("QBAUTOPOST__Auth__Jwt__SigningKey", script, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_PassInstallerOptionsInAnAclFile_When_ReadingInstallPostgres()
    {
        var script = Script("install-postgres.ps1");

        Assert.Contains("--optionfile", script, StringComparison.Ordinal);
        Assert.Contains("SetAccessRuleProtection($true, $false)", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains("-Recurse -Force", script, StringComparison.Ordinal);
        // The only place a password may be handed to the installer is the option file, as name=value.
        Assert.DoesNotContain("'--superpassword'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_TrimTrailingBackslashOfBackupFolder_When_ReadingInstallPostgres()
    {
        var script = Script("install-postgres.ps1");

        Assert.Contains("$BackupFolder.TrimEnd([char]92)", script, StringComparison.Ordinal);
        Assert.Contains("-f $backupScript, $backupDir, $PgBin", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseWhileTheApiRuns_When_ReadingApplyMigrations()
    {
        var script = Script("apply-migrations.ps1");

        Assert.Contains("Get-Process", script, StringComparison.Ordinal);
        Assert.Contains("grants.sql", script, StringComparison.Ordinal);
        Assert.Contains("migrations.sql", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_StartPostgresBeforeTheApi_When_ReadingStartAll()
    {
        var script = Script("start-all.ps1");
        var postgres = script.IndexOf("Start-Postgres -Name", StringComparison.Ordinal);
        var startApi = script.IndexOf("Start-Api -", StringComparison.Ordinal);

        Assert.True(postgres > 0, "start-all.ps1 must call Start-Postgres");
        Assert.True(postgres < script.IndexOf("if (-not (Start-Api -Exe", StringComparison.Ordinal), "PostgreSQL must be up before the API starts (bootstrap admin runs once at start)");
        Assert.True(startApi > 0);
    }

    [Fact]
    public void Should_GiveTheAppRoleNoDeleteExceptUserRoles_When_ReadingGrants()
    {
        var grants = File.ReadAllText(Path.Combine(RepoRoot.Find(), "deploy", "grants.sql"));
        var statements = grants.Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        var text = string.Join(' ', statements);

        // T-1209 (dashboard-api-m12 6.6): the second DELETE is on statement_extractions, which holds no history.
        // T-1403 (M14 spec 3.2): the third is qb_list_syncs, pruned to the newest HistoryRows on each insert.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, "DELETE", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
        Assert.Contains("GRANT DELETE ON user_roles TO qbautopost_app;", text, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON statement_extractions TO qbautopost_app;", text, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT, DELETE ON qb_list_syncs TO qbautopost_app;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string Script(string file) => File.ReadAllText(Path.Combine(DeployDir, file));
    /// <summary>
    /// T-910 / FR-A-13: the key issuer prints the key once and stores only a salted hash. A script that wrote the
    /// key itself would turn clients.json from a hash list into a credential store (CLAUDE.md rule 6).
    /// </summary>
    [Fact]
    public void Should_StoreOnlyAHash_When_ReadingTheKeyIssuer()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find(), "scripts", "new-api-client.ps1"));

        Assert.Contains("SupportsShouldProcess", script, StringComparison.Ordinal);
        Assert.Contains("keyHash", script, StringComparison.Ordinal);
        // The key reaches the console and nothing else: it is never put into the object that is serialised.
        Assert.DoesNotContain("key  = $key", script, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", script, StringComparison.OrdinalIgnoreCase);
    }

}
