using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// Default gateway until the COM gateway exists. It never sends anything, so posting ends <c>partial</c> with nothing
/// recorded in the ledger. TODO(T-601): replaced by <c>QbGateway</c> (Windows) / fake switch in M6.
/// </summary>
public sealed class UnconfiguredQbGateway : IQbGateway
{
    private const string Message = "the QuickBooks gateway is not available in this build (arrives in M6)";

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
        throw new QuickBooksUnavailableException(Message);

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        throw new QuickBooksUnavailableException(Message);
}
