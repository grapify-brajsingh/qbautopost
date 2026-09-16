using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Builds the pipeline's <see cref="InvoiceExtractor"/> with the shipped T3 prompt and test doubles.</summary>
internal static class TestInvoiceExtractor
{
    public static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Invoice);

    /// <summary>Without <paramref name="hermes"/>, every T3 call answers with <c>fixtures/hermes/invoice.json</c>.</summary>
    public static InvoiceExtractor Create(IOcr? ocr = null, IHermesClient? hermes = null) =>
        new(
            ocr ?? new DisabledOcr(),
            hermes ?? new ScriptedHermes(_ => Fixtures.Read("hermes", "invoice.json")),
            Prompts);
}
