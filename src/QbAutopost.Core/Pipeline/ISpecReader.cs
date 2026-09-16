using QbAutopost.Core.Extract;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Pipeline;

/// <summary>Where the job spec came from; written to <c>spec.json.source</c> (spec FR-2).</summary>
public static class SpecSources
{
    public const string Hermes = "hermes";
    public const string Regex = "regex";
    public const string RegexFallback = "regex-fallback";
}

public sealed record SpecReadResult(JobSpec Spec, string Source);

/// <summary>Requirement → <see cref="JobSpec"/> (spec FR-2). M1 uses <see cref="RegexSpecReader"/>; M2 adds Hermes T1.</summary>
public interface ISpecReader
{
    Task<SpecReadResult> ReadAsync(JobInput input, CancellationToken ct);
}

/// <summary>M1 stand-in for Hermes T1 (plan T-103).</summary>
public sealed class RegexSpecReader : ISpecReader
{
    public async Task<SpecReadResult> ReadAsync(JobInput input, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(input.RequirementPath, ct);
        return new SpecReadResult(RegexSpecParser.Parse(text), SpecSources.Regex);
    }
}
