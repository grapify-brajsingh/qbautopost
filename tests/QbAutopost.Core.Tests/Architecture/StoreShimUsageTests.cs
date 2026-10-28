using System.Text.RegularExpressions;

namespace QbAutopost.Core.Tests.Architecture;

/// <summary>
/// T-1112: <c>SavedRequestStoreExtensions</c> keeps the 6-argument <c>SetValidationAsync</c>, which stores the v1
/// snapshot as the v2 one, for the unedited M10 contract tests only. Production code must compute the v2 judgement
/// itself (dashboard v2 spec 6.4), so no source under <c>src/</c> may name the shim or make a 6-argument call.
/// </summary>
public sealed partial class StoreShimUsageTests
{
    private const string ShimFile = "SavedRequestStoreExtensions.cs";

    [Fact]
    public void Should_NotNameStoreShim_When_ScanningSources()
    {
        var offenders = Sources()
            .Where(file => File.ReadAllText(file).Contains("SavedRequestStoreExtensions", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(SrcDir(), file))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Should_PassBothSnapshots_When_SourcesCallSetValidation()
    {
        var calls = Sources()
            .SelectMany(file => SetValidationCalls(File.ReadAllText(file))
                .Select(arguments => (File: Path.GetRelativePath(SrcDir(), file), Arguments: arguments)))
            .ToList();

        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.True(call.Arguments == 7, $"{call.File}: SetValidationAsync with {call.Arguments} arguments"));
    }

    /// <summary>The argument count of every <c>SetValidationAsync(</c> call; declarations (after a return type) are skipped.</summary>
    private static IEnumerable<int> SetValidationCalls(string source)
    {
        foreach (Match match in CallPattern().Matches(source))
        {
            if (DeclarationPattern().IsMatch(source[Math.Max(0, match.Index - 64)..match.Index]))
            {
                continue;
            }

            yield return CountArguments(source, match.Index + match.Length);
        }
    }

    /// <summary>Counts the top-level commas from just after the opening parenthesis to its match.</summary>
    private static int CountArguments(string source, int start)
    {
        var depth = 0;
        var commas = 0;
        var any = false;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}' when depth > 0:
                    depth--;
                    break;
                case ')':
                    return any ? commas + 1 : 0;
                case ',' when depth == 0:
                    commas++;
                    break;
            }

            any |= !char.IsWhiteSpace(c);
        }

        throw new InvalidOperationException("Unbalanced SetValidationAsync call.");
    }

    [GeneratedRegex(@"\bSetValidationAsync\(")]
    private static partial Regex CallPattern();

    [GeneratedRegex(@"Task<MutationResult>\s+$")]
    private static partial Regex DeclarationPattern();

    private static IEnumerable<string> Sources()
    {
        var src = SrcDir();
        return Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(src, f) && !string.Equals(Path.GetFileName(f), ShimFile, StringComparison.Ordinal));
    }

    private static bool IsBuildOutput(string src, string file) =>
        Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj");

    private static string SrcDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QbAutopost.sln")))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }

        throw new InvalidOperationException("QbAutopost.sln not found above the test output directory.");
    }
}
