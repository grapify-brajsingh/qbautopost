namespace QbAutopost.Core.Jobs;

/// <summary>The folder breaks the folder contract (spec F1); the API maps it to 400.</summary>
public sealed class InvalidJobFolderException(IReadOnlyList<string> errors)
    : Exception("Job folder is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
