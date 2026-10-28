using System.Text.Json;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

public class PayloadCanonicalizerTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Should_ProduceSameHash_When_OnlyKeyOrderAndWhitespaceDiffer()
    {
        var a = J("""{ "reference": "r", "transactions": [ { "externalId": "1", "amount": 5.00 } ] }""");
        var b = J("""{"transactions":[{"amount":5.00,"externalId":"1"}],"reference":"r"}""");

        Assert.Equal(PayloadCanonicalizer.Sha256(a), PayloadCanonicalizer.Sha256(b));
        Assert.Equal("""{"reference":"r","transactions":[{"amount":5.00,"externalId":"1"}]}""", PayloadCanonicalizer.Canonicalize(a));
    }

    [Fact]
    public void Should_ProduceSameHash_When_AmountIs184_3Or184_30()
    {
        var a = J("""{ "controlTotal": 184.3, "transactions": [ { "amount": 184.3 } ] }""");
        var b = J("""{ "controlTotal": 184.30, "transactions": [ { "amount": 184.30 } ] }""");

        Assert.Equal(PayloadCanonicalizer.Sha256(a), PayloadCanonicalizer.Sha256(b));
        Assert.Equal("""{"controlTotal":184.30,"transactions":[{"amount":184.30}]}""", PayloadCanonicalizer.Canonicalize(a));
    }

    [Fact]
    public void Should_ProduceDifferentHash_When_StringCaseDiffers()
    {
        var a = J("""{ "payee": "ABC" }""");
        var b = J("""{ "payee": "abc" }""");

        Assert.NotEqual(PayloadCanonicalizer.Sha256(a), PayloadCanonicalizer.Sha256(b));
    }

    [Fact]
    public void Should_KeepRawToken_When_AmountOverflowsDecimal()
    {
        var p = J("""{ "transactions": [ { "amount": 1e40 } ] }""");

        Assert.Equal("""{"transactions":[{"amount":1e40}]}""", PayloadCanonicalizer.Canonicalize(p));
    }

    [Fact]
    public void Should_KeepRawToken_When_NonMoneyNumber()
    {
        var p = J("""{ "other": 1.50, "transactions": [ { "qty": 2.0 } ] }""");

        Assert.Equal("""{"other":1.50,"transactions":[{"qty":2.0}]}""", PayloadCanonicalizer.Canonicalize(p));
    }

    [Fact]
    public void Should_KeepRawToken_When_AmountHasMoreThanTwoPlaces()
    {
        var p = J("""{ "controlTotal": 184.305 }""");

        Assert.Equal("""{"controlTotal":184.305}""", PayloadCanonicalizer.Canonicalize(p));
    }

    [Fact]
    public void Should_ReturnLowerHex64_When_Hashing()
    {
        var h = PayloadCanonicalizer.Sha256(J("{}"));

        Assert.Equal(64, h.Length);
        Assert.Equal(h.ToLowerInvariant(), h);
    }
}
