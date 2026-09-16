using System.Text.RegularExpressions;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// Spec §4 / §14 and CLAUDE.md rule 6 for the files under <c>deploy/</c>: Hermes is published on loopback only,
/// its key is required, no image is guessed, and the example environment file holds placeholders only.
/// Docker itself is not needed; <c>docker compose config</c> is the manual check (tracker T-801).
/// </summary>
public sealed partial class DeployFilesTests
{
    private static readonly string HermesDir = Path.Combine(RepoRoot.Find(), "deploy", "hermes");

    [Fact]
    public void Should_PublishHermesOnLoopbackOnly_When_ReadingCompose()
    {
        var ports = PortLines(Compose()).ToList();

        Assert.NotEmpty(ports);
        Assert.All(ports, p => Assert.StartsWith("127.0.0.1:", p, StringComparison.Ordinal));
        Assert.Contains(ports, p => p.StartsWith("127.0.0.1:8642:", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RequireApiServerKey_When_ReadingCompose()
    {
        Assert.Contains("${API_SERVER_KEY:?", Compose(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RequireImageWithoutDefault_When_ReadingCompose()
    {
        var compose = Compose();

        Assert.Matches(@"image:\s*""?\$\{HERMES_IMAGE:\?", compose);
        Assert.DoesNotContain("${HERMES_IMAGE:-", compose, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData("provider.env.example")]
    public void Should_UsePlaceholdersOnly_When_ReadingExampleEnvFile(string file)
    {
        var secrets = EnvEntries(File.ReadAllLines(Path.Combine(HermesDir, file)))
            .Where(e => SecretName().IsMatch(e.Key))
            .ToList();

        Assert.NotEmpty(secrets);
        Assert.All(secrets, e => Assert.StartsWith("replace-with-", e.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_DeclareEveryRequiredVariable_When_ReadingEnvExample()
    {
        var declared = EnvEntries(File.ReadAllLines(Path.Combine(HermesDir, ".env.example")))
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);
        var required = RequiredVariable().Matches(Compose()).Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(required);
        Assert.All(required, name => Assert.Contains(name, declared));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("deploy/hermes/provider.env")]
    public void Should_IgnoreRealSecretFile_When_ReadingGitignore(string pattern)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot.Find(), ".gitignore")).Select(l => l.Trim());

        Assert.Contains(pattern, lines);
    }

    [Fact]
    public void Should_KeepProviderFileOptional_When_ReadingCompose()
    {
        var compose = Compose();

        Assert.Contains("path: ./provider.env", compose, StringComparison.Ordinal);
        Assert.Contains("required: false", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_DescribeBothDockerPaths_When_ReadingHermesReadme()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot.Find(), "deploy", "README-hermes.md"));

        Assert.Contains("WSL 2", readme, StringComparison.Ordinal);
        Assert.Contains("Docker Desktop", readme, StringComparison.Ordinal);
        Assert.Contains(".wslconfig", readme, StringComparison.Ordinal);
    }

    private static string Compose() => File.ReadAllText(Path.Combine(HermesDir, "docker-compose.yml"));

    private static IEnumerable<string> PortLines(string compose)
    {
        var inPorts = false;
        foreach (var raw in compose.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed == "ports:")
            {
                inPorts = true;
                continue;
            }

            if (inPorts && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                yield return trimmed[2..].Trim().Trim('"', '\'');
                continue;
            }

            inPorts = false;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> EnvEntries(IEnumerable<string> lines) =>
        lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => new KeyValuePair<string, string>(l[..l.IndexOf('=')].Trim(), l[(l.IndexOf('=') + 1)..].Trim()));

    [GeneratedRegex("KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"\$\{([A-Z0-9_]+):\?")]
    private static partial Regex RequiredVariable();
}
