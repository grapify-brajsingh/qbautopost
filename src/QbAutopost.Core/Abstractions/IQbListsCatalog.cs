namespace QbAutopost.Core.Abstractions;

/// <summary>
/// M17 §4.7: the QuickBooks lists of every company. Each company has its own current lists and its own sync history;
/// a sync of one company never changes another's (A-8). The plain <see cref="IQbListsSource"/>/<see cref="IQbListsStore"/>
/// registrations stay the default company's view.
/// </summary>
public interface IQbListsCatalog
{
    /// <summary>The company's current lists and last attempt; memory reads only. An unknown id reads <c>QbLists.Empty</c>.</summary>
    IQbListsSource SourceFor(int companyId);

    /// <summary>Where the company's attempts are recorded and its history is read.</summary>
    IQbListsStore StoreFor(int companyId);
}
