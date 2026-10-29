using System.Globalization;
using System.Runtime.CompilerServices;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// T-1212a (Ruling R4, Q-112): Windows test runs have ICU, so the machine culture is real. Every thread is pinned to
/// the invariant culture before any test runs, so results depend neither on the machine nor on which test starts a host.
/// </summary>
internal static class InvariantTestCulture
{
#pragma warning disable CA2255 // A test assembly is the intended place for this module initializer.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Pin()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
