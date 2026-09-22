namespace QbAutopost.Core.Security;

/// <summary>
/// FR-A-17: is a path a caller sent inside a folder the owner named? Traversal is the direct risk now that callers
/// are remote (D-4) — <c>folder</c> used to be typed by the person who owned the server.
/// <para>
/// The comparison is on the <b>normalised</b> path, so <c>…/jobs/../secrets</c> is judged as <c>…/secrets</c> rather
/// than on how it was spelled, and a root must be followed by a separator to contain anything, so <c>jobs-archive</c>
/// is not inside <c>jobs</c>. Case is ignored only where the platform ignores it.
/// </para>
/// </summary>
public static class PathAllowList
{
    /// <summary>
    /// True when <paramref name="path"/> is absolute and sits inside one of <paramref name="roots"/> — or when no
    /// root is configured at all, which means the allow-list has not been set up. A caller's path is still required
    /// to be absolute in that case, so "unconfigured" never becomes "anything at all".
    /// </summary>
    public static bool IsInside(string? path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var configured = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (configured.Count == 0)
        {
            // Empty is "not configured", and only reachable on a loopback bind: startup refuses a remote bind with no
            // roots listed, so a stranger never meets this branch. A list of nothing but blanks is a typo, not a
            // decision, and is refused rather than read as "everywhere".
            return roots.Count == 0;
        }

        var candidate = Normalise(path);
        return configured.Any(root => Contains(Normalise(root), candidate));
    }

    private static bool Contains(string root, string candidate) =>
        candidate.Equals(root, Comparison)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, Comparison);

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Windows paths are case-insensitive and Linux paths are not. Comparing case-insensitively everywhere would
    /// refuse nothing extra, but it would silently accept <c>/SRV/Jobs</c> as <c>/srv/jobs</c> on a system where they
    /// are two different folders — the mirror of the platform bug T-908 shipped (handoff trap 11).
    /// </summary>
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
