using QbAutopost.Core.Security;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Security;

/// <summary>
/// T-910 / FR-A-13: per-caller keys and scopes. Once callers are remote (D-4), one shared key is no longer an
/// acceptable answer to "who asked for this posting?".
/// <para>
/// Two properties matter most here and are asserted rather than assumed: <b>only a hash is ever stored</b>, and an
/// unknown, disabled, expired or out-of-scope caller is refused — the default is no, never yes.
/// </para>
/// </summary>
public sealed class ApiClientsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private string File => Path.Combine(_dir.Root, "clients.json");

    private static ApiClient Client(string key, string id = "acme-erp", params string[] scopes)
    {
        var secret = ApiClientStore.NewSecret(key);
        return new ApiClient
        {
            Id = id,
            Name = "Acme ERP",
            KeyHash = secret.KeyHash,
            KeySalt = secret.KeySalt,
            Scopes = scopes.Length == 0 ? [ApiScopes.QbRead, ApiScopes.QbPost] : scopes,
            CreatedUtc = Now.AddDays(-1),
        };
    }

    private ApiClientResolver Resolver(params ApiClient[] clients)
    {
        new ApiClientStore(File).Save(new ApiClientList { Clients = clients });
        return new ApiClientResolver(new ApiClientStore(File).Load());
    }

    [Fact]
    public void Should_ResolveTheCaller_When_TheKeyMatches()
    {
        var resolved = Resolver(Client("s3cret-key")).Resolve("s3cret-key", Now);

        Assert.Equal("acme-erp", resolved!.Id);
    }

    [Fact]
    public void Should_ResolveNobody_When_TheKeyIsWrong()
    {
        Assert.Null(Resolver(Client("s3cret-key")).Resolve("not-the-key", Now));
    }

    [Fact]
    public void Should_ResolveNobody_When_TheKeyIsEmpty()
    {
        Assert.Null(Resolver(Client("s3cret-key")).Resolve("", Now));
    }

    [Fact]
    public void Should_ResolveNobody_When_TheClientIsDisabled()
    {
        var client = Client("s3cret-key") with { Enabled = false };

        Assert.Null(Resolver(client).Resolve("s3cret-key", Now));
    }

    [Fact]
    public void Should_ResolveNobody_When_TheClientHasExpired()
    {
        var client = Client("s3cret-key") with { ExpiresUtc = Now.AddMinutes(-1) };

        Assert.Null(Resolver(client).Resolve("s3cret-key", Now));
    }

    [Fact]
    public void Should_StillResolve_When_TheExpiryIsInTheFuture()
    {
        var client = Client("s3cret-key") with { ExpiresUtc = Now.AddDays(1) };

        Assert.NotNull(Resolver(client).Resolve("s3cret-key", Now));
    }

    [Fact]
    public void Should_RefuseTheOldKey_When_ItsRotationWindowHasClosed()
    {
        var secret = ApiClientStore.NewSecret("old-key");
        var rotated = Client("new-key") with
        {
            PreviousKeyHash = secret.KeyHash,
            PreviousKeySalt = secret.KeySalt,
            PreviousExpiresUtc = Now.AddMinutes(-1),
        };

        Assert.Null(Resolver(rotated).Resolve("old-key", Now));
        Assert.NotNull(Resolver(rotated).Resolve("new-key", Now));
    }

    [Fact]
    public void Should_AcceptTheOldKey_When_ItsRotationWindowIsOpen()
    {
        // FR-A-13 rotation: a caller must be able to roll a key over without a window where neither key works.
        var secret = ApiClientStore.NewSecret("old-key");
        var rotated = Client("new-key") with
        {
            PreviousKeyHash = secret.KeyHash,
            PreviousKeySalt = secret.KeySalt,
            PreviousExpiresUtc = Now.AddHours(1),
        };

        Assert.Equal("acme-erp", Resolver(rotated).Resolve("old-key", Now)!.Id);
    }

    [Fact]
    public void Should_NeverWriteTheKeyItself_When_AClientIsSaved()
    {
        // CLAUDE.md rule 6. The whole point of a salted hash is that this file leaking is not a breach.
        Resolver(Client("s3cret-key"));

        Assert.DoesNotContain("s3cret-key", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_GiveTwoClientsDifferentHashes_When_TheyChoseTheSameKey()
    {
        // Distinct salts, so one stolen hash does not identify every caller using the same key.
        var a = ApiClientStore.NewSecret("same-key");
        var b = ApiClientStore.NewSecret("same-key");

        Assert.NotEqual(a.KeySalt, b.KeySalt);
        Assert.NotEqual(a.KeyHash, b.KeyHash);
    }

    [Fact]
    public void Should_HaveTheScope_When_ItIsListed()
    {
        var client = Client("k", scopes: [ApiScopes.QbRead]);

        Assert.True(client.Allows(ApiScopes.QbRead));
        Assert.False(client.Allows(ApiScopes.QbPost));
    }

    [Fact]
    public void Should_HaveEveryScope_When_TheClientIsAdmin()
    {
        var client = Client("k", scopes: [ApiScopes.Admin]);

        Assert.True(client.Allows(ApiScopes.QbPost));
        Assert.True(client.Allows(ApiScopes.RulesWrite));
    }

    [Fact]
    public void Should_AllowAnyAddress_When_NoCidrIsListed()
    {
        Assert.True(Client("k").AllowsAddress(System.Net.IPAddress.Parse("203.0.113.9")));
    }

    [Fact]
    public void Should_RefuseAnAddressOutsideTheList_When_CidrsAreListed()
    {
        var client = Client("k") with { AllowedCidrs = ["10.0.0.0/24"] };

        Assert.True(client.AllowsAddress(System.Net.IPAddress.Parse("10.0.0.7")));
        Assert.False(client.AllowsAddress(System.Net.IPAddress.Parse("10.0.1.7")));
    }

    [Fact]
    public void Should_RefuseAnUnknownAddress_When_CidrsAreListed()
    {
        // A request with no remote address cannot be shown to be inside the allow-list, so it is outside it.
        var client = Client("k") with { AllowedCidrs = ["10.0.0.0/24"] };

        Assert.False(client.AllowsAddress(null));
    }

    [Fact]
    public void Should_IgnoreAnUnparsableCidr_When_TheRestAreFine()
    {
        // A typo in one entry must not silently widen the list to everything, nor take the API down.
        var client = Client("k") with { AllowedCidrs = ["not-a-cidr", "10.0.0.0/24"] };

        Assert.True(client.AllowsAddress(System.Net.IPAddress.Parse("10.0.0.7")));
        Assert.False(client.AllowsAddress(System.Net.IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Should_AcceptAKeyIssuedByTheOperatorScript_When_ItsHashIsChecked()
    {
        // A test vector, not a credential: these three values were produced by one run of
        // scripts/new-api-client.ps1 against a throw-away file, and pin that the PowerShell hash (SHA-256 over
        // salt || key, base64) is the one ApiClientStore computes. If they ever disagree, every key the script
        // issues would be rejected by the app - a silent lockout that nothing else here would catch.
        const string key = "i15ZNLxVmqD_4Tw8hMyQeRjiu2QhcEFd7LdOC1NF9NU";
        const string keyHash = "FbvIgE4AxyFMk+5KofqMoCjJmfj+tb6yfn7ZPzML6Vo=";
        const string keySalt = "mnDolDNl0S1Y6u9Xomw5rQ==";

        Assert.True(ApiClientStore.Matches(key, keyHash, keySalt));
        Assert.False(ApiClientStore.Matches(key + "x", keyHash, keySalt));
    }

    [Fact]
    public void Should_LoadNothing_When_TheFileIsMissing()
    {
        Assert.Empty(new ApiClientStore(File).Load().Clients);
    }

    [Fact]
    public void Should_LoadNothing_When_TheFileIsUnreadable()
    {
        Directory.CreateDirectory(_dir.Root);
        System.IO.File.WriteAllText(File, "{ not json");

        // Fail closed: an unreadable client list means nobody is admitted, not everybody.
        Assert.Empty(new ApiClientStore(File).Load().Clients);
    }
}
