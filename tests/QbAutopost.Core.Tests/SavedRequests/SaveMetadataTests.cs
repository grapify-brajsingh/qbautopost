using System.Text.Json;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

/// <summary>T-1104 / dashboard v2 spec 5.1 and 5.2: metadata read from the original body.</summary>
public sealed class SaveMetadataTests
{
    private static SaveMetadata Read(string json) => SaveMetadataReader.Read(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Should_ReadAllFields_When_EmailIsComplete()
    {
        var m = Read("""
            {"pushId":"3F2504E0-4F89-11D3-9A0C-0305E82C3301","email":{"subject":"Invoices","receivedUtc":"2026-10-27T07:12:44+02:00","from":"a@b.com","messageId":"<x@y>"}}
            """);

        Assert.Equal("Invoices", m.EmailSubject);
        Assert.Equal(new DateTime(2026, 10, 27, 5, 12, 44, DateTimeKind.Utc), m.EmailReceivedUtc);
        Assert.Equal(DateTimeKind.Utc, m.EmailReceivedUtc!.Value.Kind);
        Assert.Equal("a@b.com", m.EmailFrom);
        Assert.Equal("<x@y>", m.EmailMessageId);
        Assert.Equal(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), m.PushId);
        Assert.True(m.HasEmail);
        Assert.Empty(m.MetadataErrors);
    }

    [Fact]
    public void Should_TruncateSubject_When_Over998Chars()
    {
        var subject = new string('a', 1200);
        var m = Read(JsonSerializer.Serialize(new { email = new { subject } }));

        Assert.Equal(998, m.EmailSubject!.Length);
    }

    [Fact]
    public void Should_NotSplitSurrogatePair_When_Char998IsHighSurrogate()
    {
        var subject = new string('a', 997) + "\U0001F600" + "tail";
        var m = Read(JsonSerializer.Serialize(new { email = new { subject } }));

        Assert.Equal(997, m.EmailSubject!.Length);
        Assert.False(char.IsHighSurrogate(m.EmailSubject[^1]));
    }

    [Fact]
    public void Should_LeaveReceivedNull_When_NoOffset()
    {
        var m = Read("""{"email":{"subject":"s","receivedUtc":"2026-10-27T07:12:44"}}""");

        Assert.Null(m.EmailReceivedUtc);
        Assert.Equal("s", m.EmailSubject);
        Assert.Empty(m.MetadataErrors);
    }

    [Fact]
    public void Should_IgnoreEmail_When_NotObject()
    {
        var m = Read("""{"email":"hello"}""");

        Assert.False(m.HasEmail);
        Assert.Empty(m.MetadataErrors);
    }

    [Fact]
    public void Should_LeaveFieldNull_When_FieldHasWrongType()
    {
        var m = Read("""{"email":{"subject":5,"from":"x"}}""");

        Assert.Null(m.EmailSubject);
        Assert.Equal("x", m.EmailFrom);
    }

    [Fact]
    public void Should_ParsePushId_When_UuidWithoutDashes()
    {
        var m = Read("""{"pushId":"3F2504E04F8911D39A0C0305E82C3301"}""");

        Assert.Equal(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), m.PushId);
        Assert.Empty(m.MetadataErrors);
    }

    [Fact]
    public void Should_RecordError_When_PushIdNotUuid()
    {
        var m = Read("""{"pushId":"nope"}""");

        Assert.Null(m.PushId);
        Assert.Equal(new[] { "$.pushId: not a UUID" }, m.MetadataErrors);
    }

    [Fact]
    public void Should_RecordError_When_PushIdIsNumber()
    {
        var m = Read("""{"pushId":42}""");

        Assert.Null(m.PushId);
        Assert.Equal(new[] { "$.pushId: not a UUID" }, m.MetadataErrors);
    }

    [Fact]
    public void Should_RecordNoError_When_PushIdMissingOrNull()
    {
        Assert.Empty(Read("""{}""").MetadataErrors);
        Assert.Empty(Read("""{"pushId":null}""").MetadataErrors);
    }
}
