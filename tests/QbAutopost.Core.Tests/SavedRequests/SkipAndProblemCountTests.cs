using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

/// <summary>T-1104 / dashboard v2 spec 4.1: skip count and problem count.</summary>
public sealed class SkipAndProblemCountTests
{
    [Fact]
    public void Should_CountOnlyLiteralFalse_When_SkipCounted()
    {
        var payload = JsonDocument.Parse(
            """{"transactions":[{"post":false},{"post":"false"},{"post":0},{"post":true},{},{"post":false},5]}""").RootElement;

        Assert.Equal(2, SavedRequestColumns.SkipCountOf(payload));
    }

    [Fact]
    public void Should_ReturnZero_When_TransactionsNotArray()
    {
        Assert.Equal(0, SavedRequestColumns.SkipCountOf(JsonDocument.Parse("""{"transactions":1}""").RootElement));
    }

    [Fact]
    public void Should_SumFindingsAndErrors_When_ProblemCounted()
    {
        var finding = new SyncPostFinding(0, null, "r", "d", null, null);
        var snapshot = new ValidationSnapshot(
            ValidationState.Held, DateTime.UtcNow, 1, null, [finding, finding], ["e1", "e2", "e3"], null);

        Assert.Equal(5, snapshot.ProblemCount);
    }
}
