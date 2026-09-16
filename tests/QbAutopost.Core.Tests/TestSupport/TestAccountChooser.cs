using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Mapping;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Builds the pipeline's <see cref="AccountChooser"/> with the shipped T4 prompt and a test Hermes.</summary>
internal static class TestAccountChooser
{
    public static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Account);

    /// <summary>Without <paramref name="hermes"/>, every T4 call answers with <c>fixtures/hermes/account.json</c>.</summary>
    public static AccountChooser Create(IHermesClient? hermes = null) =>
        new(hermes ?? new ScriptedHermes(_ => Fixtures.Read("hermes", "account.json")), Prompts);
}
