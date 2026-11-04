using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Api.Companies;

/// <summary>
/// M17 §3: the admin's Check access. Opens a session on the company's file, asks <c>CompanyQueryRq</c> and turns the
/// answer into a <see cref="CompanyAccessCheck"/> for the store.
/// </summary>
public sealed class CompanyAccessChecker(IQbGateway gateway, PipelineOptions options, IClock clock)
{
    public const string BusyMessage =
        "QuickBooks did not answer within the busy timeout; answer the certificate dialog on the server, then check again.";

    /// <summary>
    /// The one call that may open a session for a company that is not approved (spec §4.7). Returns <c>FileNotFound</c>
    /// without a session when the file is gone. A refused session (another file open, or the file could not be opened,
    /// ruling B1) throws <see cref="QuickBooksWrongCompanyException"/> / <see cref="QuickBooksCompanyFileUnavailableException"/>:
    /// nothing is recorded and the caller answers the problem. The caller makes sure the company has a file to open.
    /// </summary>
    public async Task<CompanyAccessCheck> CheckAsync(Company company, CancellationToken ct)
    {
        var path = company.FilePath;
        if (path is null || !File.Exists(path))
        {
            return Result(company, CompanyAccess.FileNotFound, "The company file was not found on the server.", null);
        }

        try
        {
            var request = QbCompanyQuery.Build(options.QbXmlVersion);
            var answer = company.IsDefault
                ? await gateway.ProcessAsync(request, ct)
                : await gateway.ProcessAsync(new QbTarget(path), request, ct);
            var info = QbCompanyQuery.Parse(answer);
            return info.Status.Code == 0
                ? Result(company, CompanyAccess.Approved, "QuickBooks opened the company file.", info)
                : Failed(company, $"QuickBooks refused the company query ({info.Status}).", null);
        }
        catch (QuickBooksBusyException)
        {
            // SPEC conflict 3: the call is abandoned but the lock stays held until the dialog is answered (COM cannot be cancelled).
            // For the default company the caller answers this without storing it (final review ruling 3).
            return Result(company, CompanyAccess.NeedsApproval, BusyMessage, null);
        }
        catch (QuickBooksUnavailableException ex) when (CompanyFileRefusal.Of(ex) is null)
        {
            return Failed(company, ex.Message, ex);
        }
        catch (Exception ex) when (ex is QuickBooksCallException or FormatException)
        {
            return Failed(company, ex.Message, ex);
        }
    }

    /// <summary>
    /// A failed check is stored as <c>error</c>, except on the default company (final review ruling 3): its stored access
    /// is what lets an upgraded single-company install keep posting, so a failure there is never stored. It is thrown as
    /// <see cref="QuickBooksCompanyFileUnavailableException"/> and the caller answers 503 <c>company-file-unavailable</c>.
    /// </summary>
    private CompanyAccessCheck Failed(Company company, string message, Exception? inner)
    {
        if (company.IsDefault)
        {
            throw new QuickBooksCompanyFileUnavailableException(company.FilePath ?? "", message, inner);
        }

        return Result(company, CompanyAccess.Error, message, null);
    }

    private CompanyAccessCheck Result(Company company, CompanyAccess access, string message, QbCompanyInfo? info) =>
        new(access, message, info?.CompanyName, info?.LegalCompanyName, clock.UtcNow, company.FilePath);
}
