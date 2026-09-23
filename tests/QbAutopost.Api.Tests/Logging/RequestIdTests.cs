using QbAutopost.Api.Logging;

namespace QbAutopost.Api.Tests.Logging;

/// <summary>
/// T-912 / api-v1 §2.6: every request gets a 26-character, sortable, URL-safe <c>requestId</c>. An inbound
/// <c>X-Request-Id</c> is honoured only when it matches <c>^[A-Za-z0-9_-]{8,64}$</c>.
/// <para>
/// Sortable matters because the audit file (FR-A-16) is money evidence read in time order; URL-safe matters because
/// the id is echoed in a header and quoted in problem details.
/// </para>
/// </summary>
public sealed class RequestIdTests
{
    private static readonly DateTime Noon = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Should_Be26UrlSafeCharacters_When_OneIsGenerated()
    {
        var id = RequestId.New(Noon);

        Assert.Equal(26, id.Length);
        Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"'{c}' is not URL-safe"));
    }

    [Fact]
    public void Should_SortByTime_When_IdsAreGeneratedInOrder()
    {
        var earlier = RequestId.New(Noon);
        var later = RequestId.New(Noon.AddMilliseconds(1));
        var muchLater = RequestId.New(Noon.AddDays(40));

        Assert.True(string.CompareOrdinal(earlier, later) < 0, $"{earlier} should sort before {later}");
        Assert.True(string.CompareOrdinal(later, muchLater) < 0, $"{later} should sort before {muchLater}");
    }

    [Fact]
    public void Should_DifferEveryTime_When_ManyAreGeneratedInTheSameMillisecond()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => RequestId.New(Noon)).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Should_ShareTheTimePrefix_When_TwoIdsAreGeneratedInTheSameMillisecond()
    {
        var first = RequestId.New(Noon);
        var second = RequestId.New(Noon);

        Assert.Equal(first[..10], second[..10]);
    }

    [Theory]
    [InlineData("abcdefgh")]
    [InlineData("caller-request-0042")]
    [InlineData("A_b-C_9d")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void Should_AcceptAnInboundId_When_ItMatchesThePattern(string value)
    {
        Assert.True(RequestId.IsAcceptable(value), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short7c")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has.dot")]
    [InlineData("café-request")]
    public void Should_RefuseAnInboundId_When_ItIsMalformed(string? value)
    {
        Assert.False(RequestId.IsAcceptable(value));
    }

    [Fact]
    public void Should_HonourTheInboundId_When_ItIsAcceptable()
    {
        Assert.Equal("caller-request-0042", RequestId.Choose("caller-request-0042", Noon));
    }

    [Fact]
    public void Should_GenerateAFreshId_When_TheInboundOneIsMalformed()
    {
        var chosen = RequestId.Choose("no good!", Noon);

        Assert.Equal(26, chosen.Length);
        Assert.DoesNotContain(" ", chosen, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_GenerateAFreshId_When_NoneIsSent()
    {
        Assert.Equal(26, RequestId.Choose(null, Noon).Length);
    }
}
