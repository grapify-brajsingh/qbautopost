using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Hermes;

public sealed class PromptLibraryTests
{
    private static readonly string ShippedFolder = Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder);

    [Fact]
    public void Should_FillEveryPlaceholder_When_ValuesAreGiven()
    {
        var text = PromptTemplate.Fill("Use {{kinds}} or {{kinds}}; not {{other_1}}.", Values(("kinds", "A, B"), ("other_1", "C")));

        Assert.Equal("Use A, B or A, B; not C.", text);
    }

    [Fact]
    public void Should_Throw_When_PlaceholderHasNoValue()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PromptTemplate.Fill("x {{missing}}", Values()));

        Assert.Contains("{{missing}}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_LeaveOtherBraces_When_NotAPlaceholder()
    {
        Assert.Equal("{ \"a\": {} } {{ x }}", PromptTemplate.Fill("{ \"a\": {} } {{ x }}", Values()));
    }

    [Fact]
    public void Should_LoadShippedSpecPrompt_When_BuildCopiedIt()
    {
        var library = PromptLibrary.Load(ShippedFolder, HermesTask.Spec);

        var text = library.Render(HermesTask.Spec, Values(("kinds", "Check, CreditCard, Deposit"), ("companyPlaceholder", "Company")));

        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
        Assert.Contains("Check, CreditCard, Deposit", text, StringComparison.Ordinal);
        Assert.Contains("\"bankLast4\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_FailAtLoad_When_RequiredPromptIsMissing()
    {
        using var folder = new TempJobFolder();

        var ex = Assert.Throws<FileNotFoundException>(() => PromptLibrary.Load(folder.Folder, HermesTask.Spec));

        Assert.Contains("spec.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_FailAtRender_When_OptionalPromptWasNotLoaded()
    {
        using var folder = new TempJobFolder();
        var library = PromptLibrary.Load(folder.Folder);

        Assert.Throws<InvalidOperationException>(() => library.Render(HermesTask.Account, Values()));
    }

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
