using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Core.Tests.Hermes;

public sealed class JsonReplyTests
{
    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("  {\"a\":1}\n", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```JSON\r\n{\"a\":1}\r\n```\r\n", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}", "{\"a\":1}")]
    [InlineData("```{\"a\":1}```", "{\"a\":1}")]
    public void Should_StripSurroundingFence_When_Present(string content, string expected)
    {
        Assert.Equal(expected, JsonReply.StripFences(content));
    }

    [Fact]
    public void Should_LeaveTextAlone_When_FenceIsNotAtStart()
    {
        Assert.Equal("Here: ```{}```", JsonReply.StripFences("Here: ```{}```"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("{ \"value\": ")]
    public void Should_ReturnErrors_When_ReplyIsNotAnObject(string? content)
    {
        var answer = JsonReply.TryParse<Probe>(content, out var errors);

        Assert.Null(answer);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Should_ReturnValidateErrors_When_AnswerIsInvalid()
    {
        var answer = JsonReply.TryParse<Probe>("{ \"value\": \"\" }", out var errors);

        Assert.Null(answer);
        Assert.Equal(["value is required"], errors);
    }

    [Fact]
    public void Should_ReadCaseInsensitivelyWithTrailingCommas_When_AnswerIsValid()
    {
        var answer = JsonReply.TryParse<Probe>("{ \"Value\": \"x\", }", out var errors);

        Assert.Equal("x", answer?.Value);
        Assert.Empty(errors);
    }

    private sealed record Probe(string? Value) : IValidatable
    {
        public IReadOnlyList<string> Validate() =>
            string.IsNullOrEmpty(Value) ? ["value is required"] : [];
    }
}
