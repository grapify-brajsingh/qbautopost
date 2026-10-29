using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.QuickBooks;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>M14 §6: the name and session logic, run through the injected lookup so no real process is read.</summary>
[SupportedOSPlatform("windows")]
public sealed class QbPresenceTests
{
    private const int Mine = 3;

    private static bool Open(params (string Name, int SessionId)[] processes) =>
        new QbPresence(() => processes, Mine).IsCompanyOpen();

    [Theory]
    [InlineData("QBW")]
    [InlineData("QBW32")]
    [InlineData("qbw32")]
    public void Should_BeOpen_When_QuickBooksRunsInTheSameSession(string name) =>
        Assert.True(Open((name, Mine)));

    [Fact]
    public void Should_NotBeOpen_When_QuickBooksRunsOnlyInAnotherSession() =>
        Assert.False(Open(("QBW", 1)));

    [Fact]
    public void Should_NotBeOpen_When_NoQuickBooksProcessRuns() =>
        Assert.False(Open(("notepad", Mine)));

    [Fact]
    public void Should_SkipAnUnreadableEntry_When_AnotherEntryMatches()
    {
        IEnumerable<(string, int)> Lookup()
        {
            yield return ("QBW", 1);
            yield return ("QBW32", Mine);
        }

        Assert.True(new QbPresence(Lookup, Mine).IsCompanyOpen());
    }

    [Fact]
    public void Should_NotThrowOrBeOpen_When_TheLookupFails()
    {
        IEnumerable<(string, int)> Lookup() => throw new UnauthorizedAccessException();

        Assert.False(new QbPresence(Lookup, Mine).IsCompanyOpen());
    }

    [Fact]
    public void Should_AlwaysBeOpen_When_ThePresenceIsSimulated() =>
        Assert.True(new AlwaysOpenQbPresence().IsCompanyOpen());

    [Fact]
    public void Should_ResolveAlwaysOpenPresence_When_TheHostUsesTheFakeGateway()
    {
        using var factory = new ApiFactory();
        Assert.IsType<AlwaysOpenQbPresence>(factory.Services.GetRequiredService<IQbPresence>());
    }

    [Fact]
    public void Should_CountCalls_When_TheFakePresenceIsAsked()
    {
        var fake = new FakeQbPresence { IsOpen = false };
        Assert.False(fake.IsCompanyOpen());
        Assert.Equal(1, fake.Calls);
    }
}
