namespace QbAutopost.Core.Abstractions;

/// <summary>M14 §6: the guard that keeps a scheduled or dashboard sync from starting QuickBooks.</summary>
public interface IQbPresence
{
    /// <summary>
    /// True when QuickBooks is running in the API's own Windows session. It cannot tell which company file is open;
    /// <c>BeginSession</c> on <c>Company:FilePath</c> still decides that. The fake gateway and non-Windows hosts answer true.
    /// </summary>
    bool IsCompanyOpen();
}
