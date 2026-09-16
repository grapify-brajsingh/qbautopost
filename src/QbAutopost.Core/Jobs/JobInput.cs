namespace QbAutopost.Core.Jobs;

/// <summary>A job folder that passed the folder contract (spec §5), listed but not yet read.</summary>
public sealed record JobInput
{
    /// <summary>Folder name (spec §3).</summary>
    public required string JobId { get; init; }

    public required string Folder { get; init; }
    public required string RequirementPath { get; init; }

    /// <summary><c>output/</c>, created by <see cref="FolderReader.Read"/>. Never read as input (F4).</summary>
    public required string OutputDir { get; init; }

    /// <summary>Supported statement files (csv/xlsx/pdf), ordered by file name.</summary>
    public IReadOnlyList<JobFile> Statements { get; init; } = [];

    /// <summary>Supported invoice files (pdf/png/jpg), ordered by file name.</summary>
    public IReadOnlyList<JobFile> Invoices { get; init; } = [];

    /// <summary>Files and folders that were skipped, for <c>result.json.unreadable[]</c> (F2).</summary>
    public IReadOnlyList<UnreadableFile> Unreadable { get; init; } = [];
}

/// <summary>One input file. <see cref="Last4FromName"/> is the spec F5 file-name last-four, when the name has exactly one.</summary>
public sealed record JobFile(string Path, string FileName, string? Last4FromName);

/// <summary>A path relative to the job folder (forward slashes) and a reason code from <c>HoldReasons</c>.</summary>
public sealed record UnreadableFile(string File, string Reason);
