namespace QbAutopost.Data.Tests;

/// <summary>jsonb re-spaces stored text and the serialiser escapes characters, so candidate lists are compared as lists (no Docker needed).</summary>
public sealed class CompanyCandidateCompareTests
{
    [Fact]
    public void Should_TreatListsAsEqual_When_StoredJsonbTextIsRespaced()
    {
        Assert.True(EfCompanyStore.SameList("""["a.qbw", "b.qbw"]""", ["a.qbw", "b.qbw"]));
    }

    [Fact]
    public void Should_TreatListsAsEqual_When_StoredTextEscapesAnAmpersand()
    {
        Assert.True(EfCompanyStore.SameList("""["S \u0026 A.qbw"]""", ["S & A.qbw"]));
    }

    [Fact]
    public void Should_TreatListsAsDifferent_When_AFileDiffers()
    {
        Assert.False(EfCompanyStore.SameList("""["a.qbw", "b.qbw"]""", ["a.qbw", "c.qbw"]));
    }
}
