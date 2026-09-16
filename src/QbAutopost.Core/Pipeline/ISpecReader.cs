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

/// <summary><paramref name="Note"/> explains a fallback (e.g. why the Hermes answer was rejected); written to <c>spec.json.note</c>.</summary>
public sealed record SpecReadResult(JobSpec Spec, string Source, string? Note = null);

/// <summary>Requirement → <see cref="JobSpec"/> (spec FR-2): <see cref="HermesSpecReader"/>, or <see cref="RegexSpecReader"/> without Hermes.</summary>
public interface ISpecReader
{
    Task<SpecReadResult> ReadAsync(JobInput input, CancellationToken ct);
}

/// <summary>Deterministic reader without Hermes (the M1 stand-in, plan T-103); also the T1 fallback.</summary>
public sealed class RegexSpecReader : ISpecReader
{
    public async Task<SpecReadResult> ReadAsync(JobInput input, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(input.RequirementPath, ct);
        return new SpecReadResult(RegexSpecParser.Parse(text), SpecSources.Regex);
    }
}
