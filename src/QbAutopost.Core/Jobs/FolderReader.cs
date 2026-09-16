using System.Text.RegularExpressions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Jobs;

/// <summary>The folder contract (spec §5, F1/F2/F4/F5). F3 (ledger) is checked by the caller.</summary>
public static partial class FolderReader
{
    public const string RequirementFile = "requirement.txt";
    public const string StatementsDir = "statements";
    public const string InvoicesDir = "invoices";
    public const string OutputDirName = "output";

    private static readonly string[] StatementExtensions = [".csv", ".xlsx", ".pdf"];
    private static readonly string[] InvoiceExtensions = [".pdf", ".png", ".jpg"];

    /// <summary>F1: every reason the folder cannot become a job; empty when it can.</summary>
    public static IReadOnlyList<string> Validate(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return ["folder is required"];
        }

        if (!Path.IsPathFullyQualified(folder))
        {
            return [$"folder '{folder}' must be an absolute path"];
        }

        if (!Directory.Exists(folder))
        {
            return [$"folder '{folder}' does not exist"];
        }

        var errors = new List<string>();
        var jobId = JobIdOf(folder);
        if (!JobIdPattern().IsMatch(jobId))
        {
            // SPEC-GAP T-101: the id is used in URLs and batch ids ("<jobId>#<attempt>"), so only safe characters.
            errors.Add($"folder name '{jobId}' is not a valid job id (letters, digits, '.', '_', '-')");
        }

        if (!File.Exists(Path.Combine(folder, RequirementFile)))
        {
            errors.Add($"{RequirementFile} is missing");
        }

        var statements = Path.Combine(folder, StatementsDir);
        if (!Directory.Exists(statements))
        {
            errors.Add($"{StatementsDir} folder is missing");
        }
        else if (!Directory.EnumerateFiles(statements).Any())
        {
            errors.Add($"{StatementsDir} folder is empty");
        }

        return errors;
    }

    /// <summary>Lists the inputs of a valid folder and creates <c>output/</c>. Only the top level of each input folder is read (F4).</summary>
    public static JobInput Read(string folder)
    {
        var errors = Validate(folder);
        if (errors.Count > 0)
        {
            throw new InvalidJobFolderException(errors);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var unreadable = new List<UnreadableFile>();
        var statements = List(root, StatementsDir, StatementExtensions, unreadable);
        var invoices = Directory.Exists(Path.Combine(root, InvoicesDir))
            ? List(root, InvoicesDir, InvoiceExtensions, unreadable)
            : [];

        var outputDir = Path.Combine(root, OutputDirName);
        Directory.CreateDirectory(outputDir);

        return new JobInput
        {
            JobId = JobIdOf(root),
            Folder = root,
            RequirementPath = Path.Combine(root, RequirementFile),
            OutputDir = outputDir,
            Statements = statements,
            Invoices = invoices,
            Unreadable = unreadable,
        };
    }

    public static string JobIdOf(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

    private static List<JobFile> List(string root, string subDir, string[] extensions, List<UnreadableFile> unreadable)
    {
        var dir = Path.Combine(root, subDir);

        // SPEC-GAP T-101: nested folders are not read; they are reported so nothing is dropped silently.
        unreadable.AddRange(Directory.EnumerateDirectories(dir)
            .Select(d => new UnreadableFile($"{subDir}/{Path.GetFileName(d)}", HoldReasons.SubfolderIgnored))
            .OrderBy(u => u.File, StringComparer.Ordinal));

        var files = new List<JobFile>();
        foreach (var path in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                files.Add(new JobFile(path, name, Last4Detector.FromFileName(name)));
            }
            else
            {
                unreadable.Add(new UnreadableFile($"{subDir}/{name}", HoldReasons.UnsupportedExtension));
            }
        }

        return files;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex JobIdPattern();
}
