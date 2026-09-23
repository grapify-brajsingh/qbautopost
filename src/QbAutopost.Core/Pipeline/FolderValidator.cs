using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;

namespace QbAutopost.Core.Pipeline;

/// <summary>
/// api-v1 FR-A-7: what <c>POST /api/v1/jobs/validate</c> answers — F1, every statement parsed, and the G1
/// reconcile gate, for one folder.
/// </summary>
public sealed record FolderValidation
{
    /// <summary>True when the folder would become a job and no statement was held.</summary>
    public required bool Ok { get; init; }

    /// <summary>The folder as it was read (absolute, no trailing separator), or the caller's string when F1 refused it.</summary>
    public required string Folder { get; init; }

    /// <summary>F1's reasons the folder cannot become a job; empty when it can.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<StatementSummary> Statements { get; init; } = [];

    /// <summary>Files in <c>statements/</c> or <c>invoices/</c> that will not be read, and why (F4).</summary>
    public IReadOnlyList<UnreadableFile> Unreadable { get; init; } = [];
}

/// <summary>
/// FR-A-7: "would this folder run?", answered without queueing a job, without writing into the folder and without
/// a single call to QuickBooks.
/// <para>
/// It is deliberately the first two thirds of <see cref="JobPipeline"/>'s analysis and nothing more: F1 (the folder
/// contract), the statement parse, and G1. It does not read the requirement, so it never reaches G2, the mapper or
/// the duplicate check — a folder this call passes may still hold lines later, and saying so is more honest than
/// implying a clean job.
/// </para>
/// </summary>
public sealed class FolderValidator(StatementReader statements)
{
    public async Task<FolderValidation> ValidateAsync(string? folder, Rules rules, CancellationToken ct)
    {
        var errors = FolderReader.Validate(folder);
        if (errors.Count > 0)
        {
            return new FolderValidation { Ok = false, Folder = folder ?? string.Empty, Errors = errors };
        }

        // FR-A-7: no output/ in the caller's folder. A PDF statement still produces a Hermes audit copy, so it is
        // written to a temp folder of our own and deleted — the evidence belongs to a job, and there is no job here.
        var input = FolderReader.Read(folder!, createOutput: false);
        var audit = Path.Combine(Path.GetTempPath(), "qbautopost-validate", Guid.NewGuid().ToString("N"));
        var summaries = new List<StatementSummary>();
        try
        {
            foreach (var file in input.Statements)
            {
                summaries.Add(StatementCheck.Reconcile(await statements.ReadAsync(file, rules, audit, ct), file));
            }
        }
        finally
        {
            Discard(audit);
        }

        return new FolderValidation
        {
            Ok = summaries.TrueForAll(s => !s.IsHeld),
            Folder = input.Folder,
            Statements = summaries,
            Unreadable = input.Unreadable,
        };
    }

    /// <summary>A temp folder that will not delete is not worth failing a validation over; the answer is still true.</summary>
    private static void Discard(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left under the machine's temp directory; nothing in it is a secret (spec §14 keeps keys out of audits).
        }
    }
}
