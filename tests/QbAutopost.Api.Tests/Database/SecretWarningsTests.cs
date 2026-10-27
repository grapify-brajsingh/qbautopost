using Microsoft.Extensions.Configuration;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Database;

/// <summary>Saved-requests spec 4.2: a secret in appsettings.json is named by key, never by value (CLAUDE.md rule 6).</summary>
public sealed class SecretWarningsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private IConfigurationRoot FromFile(string json)
    {
        var path = _dir.Combine("appsettings.json");
        File.WriteAllText(path, json);
        return new ConfigurationBuilder().AddJsonFile(path).Build();
    }

    [Fact]
    public void Should_LogKeyNotValue_When_ConnectionStringWithPasswordInAppSettings()
    {
        var config = FromFile("""{"Database":{"ConnectionString":"Host=db.internal;Username=app;Password=hunter2"}}""");
        var log = new ListLogger<SecretWarningsTests>();

        SecretWarnings.Warn(config, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("Database:ConnectionString", entry.Message);
        Assert.Contains("QBAUTOPOST__Database__ConnectionString", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message);
        Assert.DoesNotContain("db.internal", entry.Message);
    }

    [Fact]
    public void Should_NotWarn_When_ConnectionStringHasNoPassword()
    {
        var config = FromFile("""{"Database":{"ConnectionString":"Host=/var/run/postgresql;Username=app"}}""");
        var log = new ListLogger<SecretWarningsTests>();

        SecretWarnings.Warn(config, log);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Should_NameSigningKeyAndBootstrapPassword_When_TheyAreInAppSettings()
    {
        var config = FromFile("""{"Auth":{"Jwt":{"SigningKey":"super-secret-signing-key-value"},"BootstrapAdmin":{"Password":"Admin-pass-1234"}}}""");
        var log = new ListLogger<SecretWarningsTests>();

        SecretWarnings.Warn(config, log);

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("Auth:Jwt:SigningKey", log.Text);
        Assert.Contains("Auth:BootstrapAdmin:Password", log.Text);
        Assert.DoesNotContain("super-secret", log.Text);
        Assert.DoesNotContain("Admin-pass", log.Text);
    }

    [Fact]
    public void Should_NotWarn_When_TheSecretsComeFromTheEnvironment()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=db;Password=x",
                ["Auth:Jwt:SigningKey"] = "k",
            })
            .Build();
        var log = new ListLogger<SecretWarningsTests>();

        SecretWarnings.Warn(config, log);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Should_NotWarn_When_SecretsAreEmptyInAppSettings()
    {
        var config = FromFile("""{"Database":{"ConnectionString":""},"Auth":{"Jwt":{"SigningKey":""},"BootstrapAdmin":{"Password":""}}}""");
        var log = new ListLogger<SecretWarningsTests>();

        SecretWarnings.Warn(config, log);

        Assert.Empty(log.Entries);
    }
}
