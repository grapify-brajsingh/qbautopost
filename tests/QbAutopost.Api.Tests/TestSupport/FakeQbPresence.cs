using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>Stands in for the process check (M14 §6): a test sets <see cref="IsOpen"/> and reads <see cref="Calls"/>.</summary>
public sealed class FakeQbPresence : IQbPresence
{
    public bool IsOpen { get; set; } = true;

    public int Calls { get; private set; }

    public bool IsCompanyOpen()
    {
        Calls++;
        return IsOpen;
    }
}
