using System.Text.RegularExpressions;

namespace QbAutopost.Core.Companies;

/// <summary>M17 spec §4.2: one level under Companies:RootFolder, one company per subfolder holding a .qbw.</summary>
public sealed class CompanyFolderScanner(ICompanyFolderSource source)
{
    private static readonly Regex CodePrefix = new(@"^(\d+)_(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ScanResult Scan(string rootFolder)
    {
        var folders = new List<ScannedFolder>();
        var none = new List<string>();
        try
        {
            foreach (var name in source.Subfolders(rootFolder))
            {
                var files = source.CompanyFiles(Path.Combine(rootFolder, name));
                if (files.Count == 0) { none.Add(name); continue; }
                var (code, display) = ParseFolderName(name);
                folders.Add(new ScannedFolder(name, code, display, files));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CompanyRootUnavailableException(rootFolder, e);
        }
        return new ScanResult(folders, none);
    }

    public static (string? Code, string DisplayName) ParseFolderName(string folderName)
    {
        var m = CodePrefix.Match(folderName);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.Trim()) : (null, folderName.Trim());
    }

    /// <summary>
    /// Compares a scan with the known companies. Folder identity is case-insensitive (ruling B10). A known folder with
    /// several files always yields an Update so the stored candidates are refreshed. A known folder that now holds no
    /// .qbw is treated as missing. // SPEC-GAP T-1702: spec §4.2 does not say what a folder without a .qbw means (Q-158).
    /// </summary>
    public static ScanPlan Reconcile(ScanResult scan, IReadOnlyList<KnownCompanyFolder> known)
    {
        var scanned = new Dictionary<string, ScannedFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in scan.Folders) scanned.TryAdd(f.FolderName, f);

        var add = new List<FolderChange>();
        var update = new List<FolderChange>();
        var missing = new List<int>();

        var knownByFolder = new Dictionary<string, KnownCompanyFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in known.Where(k => !string.IsNullOrEmpty(k.FolderName))) knownByFolder.TryAdd(k.FolderName, k);

        foreach (var f in scanned.Values.Where(f => !knownByFolder.ContainsKey(f.FolderName)))
        {
            add.Add(f.Files.Count == 1
                ? new FolderChange(0, f, f.Files[0], CompanyState.Active)
                : new FolderChange(0, f, null, CompanyState.Ambiguous));
        }

        foreach (var k in knownByFolder.Values)
        {
            if (!scanned.TryGetValue(k.FolderName, out var f))
            {
                if (k.State is not (CompanyState.Missing or CompanyState.Disabled)) missing.Add(k.Id);
                continue;
            }
            var change = Decide(k, f);
            if (change is not null) update.Add(change);
        }

        return new ScanPlan(add, update, missing, scan.NoCompanyFile);
    }

    private static FolderChange? Decide(KnownCompanyFolder k, ScannedFolder f)
    {
        var current = k.FilePath;
        var multi = f.Files.Count > 1;
        var chosenStillThere = current is not null && f.Files.Any(p => SamePath(p, current));

        string? path;
        CompanyState state;
        if (!multi)
        {
            var only = f.Files[0];
            if (chosenStillThere) { path = current; state = CompanyState.Active; }
            else if (k.IsDefault && current is not null) { path = current; state = CompanyState.Ambiguous; }
            else { path = only; state = CompanyState.Active; }
        }
        else if (chosenStillThere) { path = current; state = CompanyState.Active; }
        else { path = k.IsDefault ? current : null; state = CompanyState.Ambiguous; }

        if (k.State == CompanyState.Disabled)
        {
            // Disabled stays disabled; only the file may move, and only when it cannot be in doubt.
            state = CompanyState.Disabled;
            if (multi && !chosenStillThere) path = current;
            if (!multi && SamePath(path, current)) return null;
            return new FolderChange(k.Id, f, path, state);
        }

        if (!multi && state == k.State && SamePath(path, current)) return null;
        return new FolderChange(k.Id, f, path, state);
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
