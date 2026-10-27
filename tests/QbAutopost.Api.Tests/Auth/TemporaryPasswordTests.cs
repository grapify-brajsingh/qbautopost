using QbAutopost.Api.Auth;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>T-1011 (saved-requests spec 11.4): the one-time temporary password an admin hands over.</summary>
public sealed class TemporaryPasswordTests
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    [Fact]
    public void Should_BeSixteenCharactersFromTheUnambiguousAlphabet_When_Generated()
    {
        var password = TemporaryPassword.Generate(minLength: 12);

        Assert.Equal(16, password.Length);
        Assert.All(password, c => Assert.Contains(c, Alphabet));
    }

    [Fact]
    public void Should_NotRepeat_When_GeneratedManyTimes()
    {
        var passwords = Enumerable.Range(0, 200).Select(_ => TemporaryPassword.Generate(12)).ToHashSet();

        Assert.Equal(200, passwords.Count);
    }

    [Fact]
    public void Should_MeetTheConfiguredMinimum_When_ItIsLongerThanSixteen()
    {
        Assert.Equal(24, TemporaryPassword.Generate(minLength: 24).Length);
    }

    [Fact]
    public void Should_UseEveryKindOfCharacterEventually_When_ManyAreGenerated()
    {
        var all = string.Concat(Enumerable.Range(0, 500).Select(_ => TemporaryPassword.Generate(12)));

        Assert.Contains(all, char.IsUpper);
        Assert.Contains(all, char.IsLower);
        Assert.Contains(all, char.IsDigit);
        Assert.DoesNotContain('0', all);
        Assert.DoesNotContain('O', all);
        Assert.DoesNotContain('l', all);
        Assert.DoesNotContain('I', all);
        Assert.DoesNotContain('1', all);
    }
}
