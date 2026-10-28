using System.Security.Cryptography;
using System.Text.Json.Nodes;
using QbAutopost.Api.Extraction;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212b (dashboard-api-m12 6.3): the embedded prompt file. Its SHA-256 is pinned together with its version, so a
/// change to the prompt or the schema without a new <c>promptVersion</c> fails here.
/// </summary>
public sealed class ExtractionPromptTests
{
    /// <summary>The committed (LF) bytes of <c>extraction-prompt.json</c> at <see cref="PinnedVersion"/>.</summary>
    private const string PinnedSha256 = "ca64cc196a6197631fd81b211d9095ebd9324dfe94132557689ff5e867a0b19c";

    private const string PinnedVersion = "2026-10-28.api-1";

    private static readonly char Star = (char)0x2605;

    [Fact]
    public void Should_PinPromptFileHash_When_VersionUnchanged()
    {
        // Git may check the file out with CRLF (.gitattributes: text=auto); the pin is of the committed LF bytes.
        var bytes = ExtractionPrompt.RawBytes().Where(b => b != (byte)'\r').ToArray();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(PinnedVersion, ExtractionPrompt.Load().PromptVersion);
        Assert.Equal(PinnedSha256, actual);
    }

    [Fact]
    public void Should_HoldSpecPromptWithoutMarks_When_Loaded()
    {
        var prompt = ExtractionPrompt.Load().SystemPrompt;

        Assert.StartsWith("You are an expert extraction algorithm.\nOnly extract relevant information from the text.\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Star, prompt);
        Assert.Contains("omit the attribute. Never guess or use a placeholder value.", prompt, StringComparison.Ordinal);
        Assert.Contains("3. 'kind': MUST ONLY BE 'Deposit' OR 'Check'.", prompt, StringComparison.Ordinal);
        Assert.Contains("If the header does not show them, omit 'account'.", prompt, StringComparison.Ordinal);
        Assert.Contains("If the category is not clear from the description, omit 'lineAccount'.", prompt, StringComparison.Ordinal);
        Assert.Contains("If no counterparty can be identified, omit 'payee'.", prompt, StringComparison.Ordinal);
        Assert.EndsWith("9. 'last4': Extract card or account last 4 digits if present in text, otherwise null.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask My Accountant", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_HoldAppendixASchema_When_Loaded()
    {
        var schema = ExtractionPrompt.Load().Schema;

        Assert.Equal("""["is_bank_statement","transactions"]""", schema["required"]!.ToJsonString());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var items = schema["properties"]!["transactions"]!["items"]!;
        Assert.Equal("""["kind","amount"]""", items["required"]!.ToJsonString());
        Assert.False(items["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("""["string","null"]""", items["properties"]!["last4"]!["type"]!.ToJsonString());
        Assert.Equal("""["string","null"]""", items["properties"]!["checkNo"]!["type"]!.ToJsonString());
        Assert.Equal("""["Deposit","Check"]""", items["properties"]!["kind"]!["enum"]!.ToJsonString());
        Assert.Equal(
            "A statement reference identifier printed on the statement, if any. Omit if none.",
            schema["properties"]!["reference"]!["description"]!.GetValue<string>());
        Assert.DoesNotContain("Ask My Accountant", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("3188", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("zemen", schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_HandOutACopy_When_SchemaRead()
    {
        var prompt = ExtractionPrompt.Load();

        prompt.Schema["changed"] = true;

        Assert.Null(prompt.Schema["changed"]);
        Assert.True(JsonNode.DeepEquals(prompt.Schema, prompt.Schema));
    }
}
