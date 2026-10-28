using System.Text.RegularExpressions;
using Npgsql;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1106: <c>MigrationScriptTests</c> checks the tables of deploy/migrations.sql against deploy/grants.sql, not the
/// views; these cover the view <c>audit_events</c>: the app role may read it and nothing more.
/// </summary>
public sealed class AuditViewGrantTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void Should_GrantSelectOnAuditView_When_ScriptsGenerated()
    {
        var migrations = File.ReadAllText(PostgresFixture.RepoFile("deploy", "migrations.sql"));
        var grants = string.Join(
            '\n',
            File.ReadAllLines(PostgresFixture.RepoFile("deploy", "grants.sql"))
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var views = Regex.Matches(migrations, @"CREATE (?:OR REPLACE )?VIEW ""?(\w+)""?")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { "audit_events" }, views);
        Assert.All(views, v => Assert.Matches($@"GRANT SELECT ON {v} TO qbautopost_app;", grants));
        Assert.All(views, v => Assert.DoesNotMatch($@"GRANT [^;]*(INSERT|UPDATE|DELETE)[^;]*\b{v}\b", grants));
    }

    [PostgresFact]
    public async Task Should_AllowSelectOnAuditView_When_ConnectedAsAppRole()
    {
        await AsAppRoleAsync("SELECT count(*) FROM audit_events");
    }

    [PostgresFact]
    public async Task Should_RefuseDropOfAuditView_When_ConnectedAsAppRole()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AsAppRoleAsync("DROP VIEW audit_events"));

        Assert.Equal("42501", ex.SqlState);
    }

    // A UNION ALL view refuses INSERT, UPDATE and DELETE for anyone (55000) before privileges are looked at, so the
    // grant itself is checked: the role must not hold the privilege even if the view became writable.
    [PostgresTheory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    [InlineData("TRUNCATE")]
    [InlineData("TRIGGER")]
    public async Task Should_HoldNoWritePrivilegeOnAuditView_When_Granted(string privilege)
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT has_table_privilege(@role, 'audit_events', @privilege)", connection);
        command.Parameters.AddWithValue("role", PostgresFixture.AppRole);
        command.Parameters.AddWithValue("privilege", privilege);

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task AsAppRoleAsync(string sql)
    {
        var (_, app) = await postgres.CreateAppRoleDatabaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(app) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
