using System.Reflection;
using QbAutopost.Core.Api;
using QbAutopost.Core.Security;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// T-916: nothing in the public API may be decorative. A field a caller can set, or a scope an operator can grant,
/// must be read by production code — or be listed below as deliberately reserved, with the question that settles it.
/// <para>
/// This exists because of T-915. <c>allowModelAccounts</c> shipped through eight tasks declared on
/// <see cref="DirectRequest"/>, described in a hand-authored OpenAPI document as working, given a scope of its own,
/// and read by no line of production code. The drift test T-913 added compares <i>routes</i>, not <i>semantics</i>,
/// so it saw nothing wrong. A caller who set the flag was told nothing; their rows were simply held.
/// </para>
/// <para>
/// The point is not that a token scan is clever — see the evidence file for what it cannot catch. It is that adding
/// a no-op now costs a deliberate, visible act: writing your own name into <see cref="ReservedScopes"/> with a
/// tracker question beside it.
/// </para>
/// </summary>
public sealed class DecorativeSurfaceTests
{
    /// <summary>
    /// Scopes that exist but guard nothing yet. Every entry must name the tracker question that settles it, so a
    /// reservation is a recorded decision rather than somewhere to park a mistake.
    /// </summary>
    private static readonly Dictionary<string, string> ReservedScopes = new(StringComparer.Ordinal)
    {
        [nameof(ApiScopes.QbPostAi)] =
            "Q-51 and Q-71: model-chosen accounts are refused outright (T-915), so this scope guards nothing. "
            + "It is kept because the spec lists it, and removing it would change the contract while Q-51 is open.",
    };

    [Fact]
    public void Should_ReadEveryRequestField_When_ScanningProductionSources()
    {
        // Reflection, not a hand-written list: a field added tomorrow is covered without anybody remembering to.
        var fields = Properties(typeof(DirectRequest)).Concat(Properties(typeof(DirectRow)));
        var sources = ProductionSources(except: "DirectRequest.cs");

        var unread = fields
            .Where(name => !sources.Any(text => text.Contains("." + name, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(unread);
    }

    [Fact]
    public void Should_UseEveryScope_When_ScanningProductionSources()
    {
        var sources = ProductionSources(except: "ApiClient.cs");

        var unused = DeclaredScopes()
            .Where(name => !ReservedScopes.ContainsKey(name))
            .Where(name => !sources.Any(text => text.Contains("ApiScopes." + name, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(unused);
    }

    [Fact]
    public void Should_NameATrackerQuestion_ForEveryReservedScope()
    {
        // A reservation without a question is just a no-op with paperwork.
        var unexplained = ReservedScopes
            .Where(entry => !entry.Value.Contains("Q-", StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();

        Assert.Empty(unexplained);
    }

    [Fact]
    public void Should_NameARealScope_When_OneIsReserved()
    {
        // Guards the guard: a typo here would silently excuse a scope that does not exist, and quietly stop
        // protecting the one that does.
        var declared = DeclaredScopes().ToHashSet(StringComparer.Ordinal);

        Assert.All(ReservedScopes.Keys, name => Assert.Contains(name, declared));
    }

    private static IEnumerable<string> DeclaredScopes() =>
        typeof(ApiScopes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => f.Name);

    private static IEnumerable<string> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

    /// <summary>Every production source but the one that declares the thing being looked for.</summary>
    private static List<string> ProductionSources(string except)
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        return Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f) && !Path.GetFileName(f).Equals(except, StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
    }

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QbAutopost.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("QbAutopost.sln was not found above " + AppContext.BaseDirectory);
    }
}
