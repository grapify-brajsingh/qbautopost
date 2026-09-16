using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Builds the pipeline's <see cref="StatementReader"/> with the shipped T2 prompt and test doubles.</summary>
internal static class TestStatementReader
{
    public static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Statement);

    /// <summary>Without <paramref name="hermes"/>, any T2 call fails the test (no PDF expected).</summary>
    public static StatementReader Create(IOcr? ocr = null, IHermesClient? hermes = null) =>
        new(
            ocr ?? new DisabledOcr(),
            new StatementLlmExtractor(
                hermes ?? new ScriptedHermes(_ => throw new InvalidOperationException("no Hermes T2 call expected")),
                Prompts));
}
