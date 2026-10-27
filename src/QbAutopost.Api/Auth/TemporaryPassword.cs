using System.Security.Cryptography;

namespace QbAutopost.Api.Auth;

/// <summary>
/// Saved-requests spec 11.4: the one-time temporary password an admin hands over. Drawn from an alphabet without the
/// look-alikes (<c>0 O 1 l I</c>) with <see cref="RandomNumberGenerator"/>, so it can be read out or typed from paper.
/// It is never logged, never stored in plain text, and returned once.
/// </summary>
public static class TemporaryPassword
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public const int Length = 16;

    /// <summary>
    /// <see cref="Length"/> characters, or <paramref name="minLength"/> when the configured minimum is longer, so the
    /// result always satisfies the Identity password options.
    /// </summary>
    public static string Generate(int minLength) =>
        new(RandomNumberGenerator.GetItems<char>(Alphabet, Math.Max(Length, minLength)));
}
