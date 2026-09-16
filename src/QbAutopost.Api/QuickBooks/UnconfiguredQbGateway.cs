using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// The gateway on a host that is not Windows and not faked (T-601). It never sends anything, so posting ends
/// <c>partial</c> ("nothing posted") with nothing recorded in the ledger.
/// </summary>
public sealed class UnconfiguredQbGateway : IQbGateway
{
    private const string Message = "the QuickBooks SDK is only available on Windows (set QuickBooks:Fake=true in Development to simulate it)";

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
        throw new QuickBooksUnavailableException(Message);

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        throw new QuickBooksUnavailableException(Message);
}
