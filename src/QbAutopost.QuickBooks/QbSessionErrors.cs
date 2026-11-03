using QbAutopost.Core.Abstractions;

namespace QbAutopost.QuickBooks;

/// <summary>
/// M17 §4.7, ruling B1: what a failed <c>BeginSession</c> becomes. Pure (no COM types), so it is tested on any OS.
/// </summary>
public static class QbSessionErrors
{
    /// <summary>
    /// SPEC-GAP T-1707: HRESULTs BeginSession returns when QuickBooks has another company file open. Empty until verified
    /// on the server; until then a failure on another company's file is <see cref="QuickBooksCompanyFileUnavailableException"/>.
    /// </summary>
    // SPEC-GAP T-1707: fill from the server checklist; empty means "another file open" is never assumed (ruling B1).
    public static IReadOnlySet<int> WrongCompanyFile { get; } = new HashSet<int>();

    /// <param name="description">The SDK error as the session describes it (<c>0xHRESULT: message</c>).</param>
    /// <param name="defaultCompany">
    /// The configured default file (<c>Company:FilePath</c>): keeps today's exception and message, so v1 and its retry
    /// are unchanged.
    /// </param>
    /// <param name="wrongCompanyCodes">The codes meaning "another file is open"; <see cref="WrongCompanyFile"/> when null.</param>
    public static QuickBooksUnavailableException FromBeginSession(
        int hresult, string companyFile, string appName, string description, Exception inner, bool defaultCompany,
        IReadOnlySet<int>? wrongCompanyCodes = null)
    {
        if (defaultCompany)
        {
            return CouldNotOpenSession(companyFile, appName, description, inner);
        }

        if ((wrongCompanyCodes ?? WrongCompanyFile).Contains(hresult))
        {
            return new QuickBooksWrongCompanyException(
                companyFile,
                openFile: null,
                $"QuickBooks has another company file open, so '{companyFile}' could not be opened ({description})",
                inner);
        }

        return new QuickBooksCompanyFileUnavailableException(companyFile, description, inner);
    }

    /// <summary>
    /// A session that could not open without a COM error (a blank file, BeginSession returning no ticket). The default
    /// company keeps today's plain (retried) exception and wording; another company's file becomes the non-retried
    /// <see cref="QuickBooksCompanyFileUnavailableException"/> (ruling B1).
    /// </summary>
    public static QuickBooksUnavailableException NoSession(string companyFile, string reason, bool defaultCompany) =>
        defaultCompany ? new QuickBooksUnavailableException(reason) : new QuickBooksCompanyFileUnavailableException(companyFile, reason);

    /// <summary>The pre-M17 session failure (retried once by <c>ResilientQbGateway</c>); the wording is unchanged.</summary>
    internal static QuickBooksUnavailableException CouldNotOpenSession(string companyFile, string appName, string description, Exception inner) =>
        new($"could not open a QuickBooks session for '{companyFile}' ({description}); is QuickBooks running with the certificate accepted for {appName}?", inner);
}
