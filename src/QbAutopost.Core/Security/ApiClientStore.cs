using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Security;

/// <summary>
/// <c>clients.json</c> (FR-A-13). The file holds salted hashes, never keys, so a copy of it does not let anyone post.
/// <para>
/// An unreadable file loads as <b>no clients</b>, not as "let everyone in". Combined with the startup check, that
/// means a corrupt client list stops the app rather than opening it.
/// </para>
/// </summary>
public sealed class ApiClientStore(string filePath)
{
    /// <summary>A generated key and the two values that are safe to store for it.</summary>
    public sealed record Secret(string KeyHash, string KeySalt);

    private const int SaltBytes = 16;

    /// <summary>Length of a generated key in random bytes, before base64url encoding.</summary>
    private const int KeyBytes = 32;

    public string FilePath { get; } = filePath;

    /// <summary>SHA-256 over <c>salt || key</c>, base64 — the comparison value, never reversible to the key.</summary>
    public static string Hash(string key, string saltBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var buffer = new byte[salt.Length + keyBytes.Length];
        salt.CopyTo(buffer, 0);
        keyBytes.CopyTo(buffer, salt.Length);
        return Convert.ToBase64String(SHA256.HashData(buffer));
    }

    /// <summary>A fresh salt and the hash of <paramref name="key"/> under it.</summary>
    public static Secret NewSecret(string key)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));
        return new Secret(Hash(key, salt), salt);
    }

    /// <summary>A new key for a caller to keep. Printed once by the operator script and never stored anywhere.</summary>
    public static string NewKey() => Base64Url(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>Constant time, so a wrong key reveals nothing by how long it took to reject.</summary>
    public static bool Matches(string key, string keyHash, string? keySalt)
    {
        if (key.Length == 0 || keyHash.Length == 0 || string.IsNullOrEmpty(keySalt))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(Hash(key, keySalt)), Convert.FromBase64String(keyHash));
        }
        catch (FormatException)
        {
            // A hand-edited hash or salt that is not base64 fails closed rather than throwing on every request.
            return false;
        }
    }

    public ApiClientList Load()
    {
        if (!File.Exists(FilePath))
        {
            return new ApiClientList();
        }

        try
        {
            return JsonSerializer.Deserialize<ApiClientList>(AtomicFile.ReadAllText(FilePath), JsonOptions.Default)
                   ?? new ApiClientList();
        }
        catch (JsonException)
        {
            // Fail closed: nobody is admitted from a file we cannot read. Startup then refuses unless a legacy key
            // is configured, which is louder — and safer — than quietly serving an empty allow-list.
            return new ApiClientList();
        }
    }

    public void Save(ApiClientList clients) => AtomicFile.WriteJson(FilePath, clients);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Matches a presented key to a caller (FR-A-13). Every path out of here is "no" unless a usable client's current
/// key — or a still-valid rotating key — matches in constant time.
/// </summary>
public sealed class ApiClientResolver(ApiClientList clients)
{
    public IReadOnlyList<ApiClient> Clients { get; } = clients.Clients;

    public ApiClient? Resolve(string presentedKey, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        foreach (var client in Clients)
        {
            if (!client.IsUsable(utcNow))
            {
                continue;
            }

            if (ApiClientStore.Matches(presentedKey, client.KeyHash, client.KeySalt))
            {
                return client;
            }

            // Rotation: the key being retired keeps working until its own window closes (FR-A-13).
            if (client.PreviousKeyHash is { } previous
                && client.PreviousExpiresUtc is { } until
                && until > utcNow
                && ApiClientStore.Matches(presentedKey, previous, client.PreviousKeySalt))
            {
                return client;
            }
        }

        return null;
    }
}
