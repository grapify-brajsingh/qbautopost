using QbAutopost.Core.Security;

namespace QbAutopost.Core.Tests.Security;

/// <summary>
/// T-911 / FR-A-17: the folders a caller may name. Until D-4 a <c>folder</c> came from the owner typing it; now it
/// comes over the network, and "any absolute path on the server" includes every place a QuickBooks backup, a log or
/// another company's statements might sit.
/// <para>
/// Every path here is built with <see cref="Path.Combine"/> under a real temp root, because a test written with
/// <c>C:\…</c> literals passes on Windows and means nothing on Linux — which is exactly how T-908's batch-id guard
/// slipped through (handoff trap 11), and <c>dotnet test</c> must pass on Linux (CLAUDE.md rule 1).
/// </para>
/// </summary>
public sealed class PathAllowListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qbautopost-allow", Guid.NewGuid().ToString("N"));

    public PathAllowListTests() => Directory.CreateDirectory(Path.Combine(_root, "jobs"));

    [Fact]
    public void Should_Allow_When_NoRootIsConfigured()
    {
        // Empty means unconfigured, which is tolerable only on a loopback bind — TransportGuard refuses to start a
        // remote bind with an empty list, so this branch is never the one a stranger meets.
        Assert.True(PathAllowList.IsInside(Path.Combine(_root, "jobs", "2026-08"), []));
    }

    [Fact]
    public void Should_Allow_When_ThePathIsUnderAConfiguredRoot()
    {
        var roots = new[] { Path.Combine(_root, "jobs") };

        Assert.True(PathAllowList.IsInside(Path.Combine(_root, "jobs", "2026-08-tropicana"), roots));
    }

    [Fact]
    public void Should_Allow_When_ThePathIsTheRootItself()
    {
        var roots = new[] { Path.Combine(_root, "jobs") };

        Assert.True(PathAllowList.IsInside(Path.Combine(_root, "jobs"), roots));
    }

    [Fact]
    public void Should_Refuse_When_ThePathIsOutsideEveryRoot()
    {
        var roots = new[] { Path.Combine(_root, "jobs") };

        Assert.False(PathAllowList.IsInside(Path.Combine(_root, "secrets"), roots));
    }

    [Fact]
    public void Should_Refuse_When_TraversalClimbsOutOfTheRoot()
    {
        var roots = new[] { Path.Combine(_root, "jobs") };
        var escape = Path.Combine(_root, "jobs", "..", "secrets");

        Assert.False(PathAllowList.IsInside(escape, roots));
    }

    [Fact]
    public void Should_Allow_When_TraversalStaysInsideTheRoot()
    {
        var roots = new[] { Path.Combine(_root, "jobs") };
        var wandering = Path.Combine(_root, "jobs", "a", "..", "b");

        Assert.True(PathAllowList.IsInside(wandering, roots));
    }

    /// <summary>A sibling whose name merely starts with the root's is not inside it — <c>jobs-archive</c> is not <c>jobs</c>.</summary>
    [Fact]
    public void Should_Refuse_When_TheFolderOnlySharesThePrefix()
    {
        var roots = new[] { Path.Combine(_root, "jobs") };

        Assert.False(PathAllowList.IsInside(Path.Combine(_root, "jobs-archive"), roots));
    }

    [Fact]
    public void Should_Refuse_When_ThePathIsRelative() =>
        Assert.False(PathAllowList.IsInside(Path.Combine("jobs", "2026-08"), [Path.Combine(_root, "jobs")]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Should_Refuse_When_ThereIsNoPathAtAll(string? path) =>
        Assert.False(PathAllowList.IsInside(path, [Path.Combine(_root, "jobs")]));

    [Fact]
    public void Should_Allow_When_AnyOfSeveralRootsContainsIt()
    {
        var roots = new[] { Path.Combine(_root, "other"), Path.Combine(_root, "jobs") };

        Assert.True(PathAllowList.IsInside(Path.Combine(_root, "jobs", "2026-08"), roots));
    }

    [Fact]
    public void Should_Refuse_When_ARootIsBlank()
    {
        // A blank entry must not be read as "the whole filesystem"; it is a typo in configuration.
        Assert.False(PathAllowList.IsInside(Path.Combine(_root, "jobs", "2026-08"), ["", "   "]));
    }

    [Fact]
    public void Should_Ignore_When_ATrailingSeparatorIsConfigured()
    {
        var roots = new[] { Path.Combine(_root, "jobs") + Path.DirectorySeparatorChar };

        Assert.True(PathAllowList.IsInside(Path.Combine(_root, "jobs", "2026-08"), roots));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
