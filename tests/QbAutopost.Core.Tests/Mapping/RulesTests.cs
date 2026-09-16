using QbAutopost.Core.Mapping;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

public sealed class RulesTests
{
    [Fact]
    public void Should_DefaultModelThresholdToPointEight_When_RulesOmitIt()
    {
        Assert.Equal(0.8, Rules.Parse("{}").ModelConfidenceThreshold);
    }

    [Fact]
    public void Should_LoadSampleThreshold_When_SampleRulesAreRead()
    {
        Assert.Equal(0.8, Fixtures.SampleRules().ModelConfidenceThreshold);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1")]
    public void Should_AcceptModelThreshold_When_InsideZeroExclusiveToOne(string value)
    {
        var rules = Rules.Parse($$"""{ "ModelConfidenceThreshold": {{value}} }""");

        Assert.InRange(rules.ModelConfidenceThreshold, 0.01, 1);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("1.01")]
    [InlineData("80")]
    public void Should_Throw_When_ModelThresholdIsOutOfRange(string value)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Rules.Parse($$"""{ "ModelConfidenceThreshold": {{value}} }"""));

        Assert.Contains("ModelConfidenceThreshold", ex.Message, StringComparison.Ordinal);
    }
}
