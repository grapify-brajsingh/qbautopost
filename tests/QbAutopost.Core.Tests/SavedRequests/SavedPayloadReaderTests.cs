using System.Text.Json;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.SavedRequests;

public class SavedPayloadReaderTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PayloadRead Fixture(string name) => SavedPayloadReader.Read(J(Fixtures.Read("saved-requests", name)));

    private static void AssertOneError(PayloadRead read, string pathPrefix)
    {
        Assert.False(read.Readable);
        Assert.Null(read.Request);
        var error = Assert.Single(read.Errors);
        Assert.StartsWith(pathPrefix, error);
    }

    [Fact]
    public void Should_BeReadable_When_PayloadIsTheSample()
    {
        var read = SavedPayloadReader.Read(J(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "samples", "validate-and-post-request.json"))));

        Assert.True(read.Readable);
        Assert.Empty(read.Errors);
        Assert.NotEmpty(read.Request!.Transactions);
    }

    [Fact]
    public void Should_NamePath_When_KindIsUnknownEnum() => AssertOneError(Fixture("unreadable-kind.json"), "$.transactions[3].kind");

    [Fact]
    public void Should_BeUnreadable_When_TransactionsIsNull() =>
        AssertOneError(Fixture("unreadable-transactions-null.json"), "$.transactions");

    [Fact]
    public void Should_BeUnreadable_When_TransactionContainsNull() =>
        AssertOneError(Fixture("unreadable-transactions-null-row.json"), "$.transactions[0]");

    [Fact]
    public void Should_BeUnreadable_When_TransactionsIsObject() =>
        AssertOneError(Fixture("unreadable-transactions-object.json"), "$.transactions");

    [Fact]
    public void Should_BeUnreadable_When_AmountOverflowsDecimal() =>
        AssertOneError(Fixture("unreadable-amount-overflow.json"), "$.transactions[0].amount");

    [Fact]
    public void Should_BeUnreadable_When_DateIsNotIso() =>
        AssertOneError(Fixture("unreadable-date.json"), "$.transactions[0].date");

    [Fact]
    public void Should_KeepDryRunForTheEngineToRefuse_When_Present()
    {
        var read = SavedPayloadReader.Read(J("""{ "controlTotal": 1, "transactions": [], "dryRun": true }"""));

        Assert.True(read.Readable);
        Assert.NotNull(read.Request!.DryRun);
    }
}
