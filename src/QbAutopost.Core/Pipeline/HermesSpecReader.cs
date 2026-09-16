using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Core.Pipeline;

/// <summary>
/// Requirement → <see cref="Models.JobSpec"/> through Hermes T1 (spec FR-2, §9.1). When the answer fails validation twice,
/// the deterministic <see cref="RegexSpecParser"/> is used and <c>spec.json.source</c> says <c>regex-fallback</c>.
/// </summary>
public sealed class HermesSpecReader(IHermesClient hermes, PromptLibrary prompts) : ISpecReader
{
    public const string HermesDir = "hermes";

    private static readonly IReadOnlyDictionary<string, string> PromptValues = new Dictionary<string, string>
    {
        ["kinds"] = string.Join(", ", SpecAnswer.KnownKinds.Keys),
        ["companyPlaceholder"] = RegexSpecParser.CompanyPlaceholder,
    };

    public async Task<SpecReadResult> ReadAsync(JobInput input, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(input.RequirementPath, ct);
        var request = new HermesRequest(
            HermesTask.Spec,
            prompts.Render(HermesTask.Spec, PromptValues),
            text,
            Path.Combine(input.OutputDir, HermesDir));

        // SPEC-GAP T-202: FR-2 falls back only after two invalid answers. An unreachable Hermes
        // (HermesUnavailableException) propagates and fails the job rather than posting on the regex reading.
        try
        {
            var answer = await hermes.CompleteJsonAsync<SpecAnswer>(request, ct);
            return new SpecReadResult(answer.ToJobSpec(), SpecSources.Hermes);
        }
        catch (HermesValidationException ex)
        {
            return new SpecReadResult(RegexSpecParser.Parse(text), SpecSources.RegexFallback, ex.Message);
        }
    }
}
