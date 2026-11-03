using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// File mode (M14 §3.4, M17 ruling B9): without a database there is one company, the default, and its lists are
/// <c>qb-lists.json</c>. Any other id is a wiring error, never someone else's lists: it throws rather than answer
/// the default company's names for another company.
/// </summary>
public sealed class FileQbListsCatalog(FileQbLists file, ICompanyResolver resolver) : IQbListsCatalog
{
    public IQbListsSource SourceFor(int companyId) => DefaultOnly(companyId);

    public IQbListsStore StoreFor(int companyId) => DefaultOnly(companyId);

    private FileQbLists DefaultOnly(int companyId) =>
        companyId == resolver.Default.Id
            ? file
            : throw new InvalidOperationException($"Without a database only the default company has lists, not company {companyId}.");
}
