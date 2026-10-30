using System.Text.Json;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// M15 §4: what the simulated company was seeded with from <c>Paths:QbLists</c> (counts only, for the startup line).
/// Development/Testing only: <see cref="QbConnection.Create"/> applies it in <see cref="QbConnectionMode.Simulated"/>,
/// which is refused anywhere else. The SDK and unavailable modes never read it.
/// </summary>
public sealed record SimulatedListsSeed(int Accounts, int Vendors, int Customers, string File)
{
    /// <summary>
    /// Loads the accounts (name and type), vendors and customers of <paramref name="file"/> into
    /// <paramref name="company"/>. A missing or unreadable file leaves the company empty, logs one Warning and returns
    /// null; it never throws, so startup goes on.
    /// </summary>
    public static SimulatedListsSeed? Apply(SimulatedQuickBooks company, string file, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(log);

        var (lists, reason) = Read(file);
        if (lists is null)
        {
            log.LogWarning("simulated QuickBooks company has no lists: {Reason}", reason);
            return null;
        }

        foreach (var account in lists.Accounts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(account?.Name))
            {
                company.Accounts.Add(new QbAccount { Name = account.Name, Type = account.Type });
            }
        }

        foreach (var vendor in (lists.Vendors ?? []).Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            company.Vendors.Add(vendor);
        }

        foreach (var customer in (lists.Customers ?? []).Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            company.Customers.Add(customer);
        }

        return new SimulatedListsSeed(company.Accounts.Count, company.Vendors.Count, company.Customers.Count, file);
    }

    private static (QbLists? Lists, string Reason) Read(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return (null, "Paths:QbLists is not set");
        }

        if (!System.IO.File.Exists(file))
        {
            return (null, $"{file} was not found");
        }

        try
        {
            return (new QbListsStore(file).Load(), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, $"{file} could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
