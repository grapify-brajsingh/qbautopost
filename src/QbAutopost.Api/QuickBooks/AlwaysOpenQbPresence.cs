using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary>M14 §6: the simulated company and every non-SDK mode have no process to look for, so they answer open.</summary>
public sealed class AlwaysOpenQbPresence : IQbPresence
{
    public bool IsCompanyOpen() => true;
}
