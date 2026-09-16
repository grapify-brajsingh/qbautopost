using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// Prompt files <c>Hermes/prompts/&lt;task&gt;.md</c>, read once at startup (spec §9). A required prompt that is missing
/// stops the host instead of failing the first job.
/// </summary>
public sealed class PromptLibrary
{
    /// <summary>Where the build copies the prompts, relative to the application folder.</summary>
    public static readonly string DefaultFolder = Path.Combine("Hermes", "prompts");

    private readonly Dictionary<HermesTask, string> _templates;

    private PromptLibrary(Dictionary<HermesTask, string> templates) => _templates = templates;

    public static PromptLibrary Load(string folder, params HermesTask[] required)
    {
        var templates = new Dictionary<HermesTask, string>();
        foreach (var task in Enum.GetValues<HermesTask>())
        {
            var path = Path.Combine(folder, FileName(task));
            if (File.Exists(path))
            {
                templates[task] = File.ReadAllText(path);
            }
            else if (required.Contains(task))
            {
                throw new FileNotFoundException($"Hermes prompt {FileName(task)} is missing from {folder}.", path);
            }
        }

        return new PromptLibrary(templates);
    }

    public static string FileName(HermesTask task) => task.ToString().ToLowerInvariant() + ".md";

    public string Render(HermesTask task, IReadOnlyDictionary<string, string> values) =>
        _templates.TryGetValue(task, out var template)
            ? PromptTemplate.Fill(template, values)
            : throw new InvalidOperationException($"Hermes prompt {FileName(task)} was not loaded.");
}
