using System.Runtime.InteropServices;
using QbAutopost.Core.Abstractions;
using QbAutopost.QuickBooks;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>M17 §4.7, ruling B1: what a failed <c>BeginSession</c> becomes.</summary>
public sealed class QbSessionErrorsTests
{
    private const int HResult = unchecked((int)0x80040408);
    private const string File = @"C:\qb\Takoma.QBW";
    private const string Description = "0x80040408: Could not start QuickBooks.";
    private static readonly COMException Inner = new("Could not start QuickBooks.", HResult);
    private static readonly IReadOnlySet<int> Listed = new HashSet<int> { HResult };

    [Fact]
    public void Should_ReturnWrongCompany_When_HResultIsInTheList()
    {
        var ex = QbSessionErrors.FromBeginSession(HResult, File, "QbAutopost", Description, Inner, defaultCompany: false, Listed);

        var wrong = Assert.IsType<QuickBooksWrongCompanyException>(ex);
        Assert.Equal(File, wrong.NeededFile);
        Assert.Null(wrong.OpenFile);
    }

    [Fact]
    public void Should_ReturnPlainUnavailable_When_DefaultCompanySessionFails()
    {
        var ex = QbSessionErrors.FromBeginSession(HResult, File, "QbAutopost", Description, Inner, defaultCompany: true);

        Assert.IsType<QuickBooksUnavailableException>(ex);
        Assert.Equal(
            @"could not open a QuickBooks session for 'C:\qb\Takoma.QBW' (0x80040408: Could not start QuickBooks.); is QuickBooks running with the certificate accepted for QbAutopost?",
            ex.Message);
    }

    [Fact]
    public void Should_ReturnPlainUnavailable_When_DefaultCompanyHResultIsInTheList()
    {
        var ex = QbSessionErrors.FromBeginSession(HResult, File, "QbAutopost", Description, Inner, defaultCompany: true, Listed);

        Assert.IsType<QuickBooksUnavailableException>(ex);
    }

    [Fact]
    public void Should_ReturnCompanyFileUnavailable_When_NonDefaultHResultIsNotInTheList()
    {
        var ex = QbSessionErrors.FromBeginSession(HResult, File, "QbAutopost", Description, Inner, defaultCompany: false);

        var unavailable = Assert.IsType<QuickBooksCompanyFileUnavailableException>(ex);
        Assert.Equal((File, Description), (unavailable.NeededFile, unavailable.SdkMessage));
        Assert.Same(Inner, unavailable.InnerException);
    }

    [Fact]
    public void Should_StartEmpty_When_NotYetVerifiedOnTheServer()
    {
        Assert.Empty(QbSessionErrors.WrongCompanyFile);
    }
}
