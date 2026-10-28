using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1016 (spec 5.4, 5.5): the checked-in <c>deploy/migrations.sql</c> is what a server runs, so it must be the script
/// the current migrations generate; and <c>deploy/grants.sql</c> must leave the application role able to run the
/// app and unable to rewrite history.
/// </summary>
public sealed class MigrationScriptTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void Should_MatchCheckedInScript_When_Regenerated()
    {
        var options = new DbContextOptionsBuilder<QbAutopostDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=qbautopost_model_only").Options;
        using var db = new QbAutopostDbContext(options);

        var generated = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        var checkedIn = File.ReadAllText(PostgresFixture.RepoFile("deploy", "migrations.sql"));

        Assert.Equal(Normalize(generated), Normalize(checkedIn));
    }

    [Fact]
    public void Should_NameEveryMigratedTableInGrants_When_ReadingScripts()
    {
        var migrations = File.ReadAllText(PostgresFixture.RepoFile("deploy", "migrations.sql"));
        var grants = string.Join(
            ' ',
            File.ReadAllLines(PostgresFixture.RepoFile("deploy", "grants.sql"))
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var tables = System.Text.RegularExpressions.Regex
            .Matches(migrations, @"CREATE TABLE (?:IF NOT EXISTS )?""?(\w+)""?")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(grants, $@"(?<![\w""]){t}(?![\w""])|""{t}"""),
            $"table {t} is created by deploy/migrations.sql but deploy/grants.sql never names it: the app role could not touch it"));
    }

    [PostgresFact]
    public async Task Should_RefuseUpdateOnVersions_When_ConnectedAsAppRole()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AsAppRoleAsync("UPDATE saved_request_versions SET note = 'x'"));

        Assert.Equal("42501", ex.SqlState);
    }

    [PostgresTheory]
    [InlineData("UPDATE saved_request_events SET kind = 'x'")]
    [InlineData("UPDATE user_audit SET kind = 'x'")]
    [InlineData("DELETE FROM saved_request_versions")]
    [InlineData("DELETE FROM saved_request_events")]
    [InlineData("DELETE FROM user_audit")]
    [InlineData("DELETE FROM saved_requests")]
    [InlineData("DELETE FROM post_attempts")]
    [InlineData("DELETE FROM users")]
    [InlineData("DELETE FROM refresh_tokens")]
    [InlineData("TRUNCATE saved_requests CASCADE")]
    [InlineData("UPDATE \"__EFMigrationsHistory\" SET \"ProductVersion\" = 'x'")]
    [InlineData("DELETE FROM \"__EFMigrationsHistory\"")]
    [InlineData("CREATE TABLE intruder (id int)")]
    [InlineData("ALTER TABLE saved_requests ADD COLUMN intruder int")]
    [InlineData("DROP TABLE user_audit")]
    public async Task Should_RefuseTheStatement_When_ConnectedAsAppRole(string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AsAppRoleAsync(sql));

        Assert.Equal("42501", ex.SqlState);
    }

    [PostgresTheory]
    [InlineData("UPDATE saved_requests SET needs_attention = needs_attention")]
    [InlineData("UPDATE post_attempts SET error = error")]
    [InlineData("UPDATE refresh_tokens SET revoked_utc = revoked_utc")]
    [InlineData("UPDATE users SET access_failed_count = access_failed_count")]
    [InlineData("SELECT count(*) FROM saved_request_versions")]
    [InlineData("SELECT count(*) FROM \"__EFMigrationsHistory\"")]
    [InlineData("DELETE FROM user_roles")]
    public async Task Should_AllowTheStatement_When_ConnectedAsAppRole(string sql)
    {
        await AsAppRoleAsync(sql);
    }

    private async Task AsAppRoleAsync(string sql)
    {
        var (_, app) = await postgres.CreateAppRoleDatabaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(app) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // The generated script carries a byte order mark and the platform's line ends; a server's psql wants neither.
    private static string Normalize(string script) =>
        script.TrimStart('﻿').Replace("\r\n", "\n").TrimEnd();
}

/// <summary>A theory skipped, like <see cref="PostgresFactAttribute"/>, when Docker cannot provide a PostgreSQL.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (PostgresFixture.SharedSkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
