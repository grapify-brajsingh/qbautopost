using System.Runtime.Versioning;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.QuickBooks;

namespace QbAutopost.Api.QuickBooks;

public enum QbConnectionMode
{
    /// <summary>The Desktop SDK over COM (Windows).</summary>
    Sdk,

    /// <summary><see cref="SimulatedQbGateway"/>: <c>QuickBooks:Fake=true</c>, Development/Testing only.</summary>
    Simulated,

    /// <summary>Not Windows and not faked: every call fails before anything is sent.</summary>
    Unavailable,

    /// <summary>A test double supplied by the test host.</summary>
    Test,
}

/// <summary>
/// The raw gateway the host talks to (T-601 DI switch). <c>Program</c> wraps it in <c>ResilientQbGateway</c>, so tests
/// replace this holder, not <see cref="IQbGateway"/>, and still get the busy timeout and retry.
/// </summary>
public sealed class QbConnection(IQbGateway gateway, QbConnectionMode mode)
{
    public IQbGateway Gateway { get; } = gateway;

    public QbConnectionMode Mode { get; } = mode;

    public static QbConnectionMode SelectMode(bool fake, bool isWindows) =>
        fake ? QbConnectionMode.Simulated
        : isWindows ? QbConnectionMode.Sdk
        : QbConnectionMode.Unavailable;

    public static QbConnection Create(AppSettings settings, IHostEnvironment environment)
    {
        var mode = SelectMode(settings.QuickBooks.Fake, OperatingSystem.IsWindows());
        switch (mode)
        {
            case QbConnectionMode.Simulated:
                // SPEC-GAP T-601: a simulated company answers "posted" with invented TxnIDs that land in the real ledger,
                // so it is refused outside Development/Testing.
                if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                {
                    throw new InvalidOperationException(
                        "QuickBooks:Fake=true is only allowed when ASPNETCORE_ENVIRONMENT is Development or Testing.");
                }

                return new QbConnection(new SimulatedQbGateway(new SimulatedQuickBooks()), mode);
            case QbConnectionMode.Sdk when OperatingSystem.IsWindows():
                return new QbConnection(CreateSdkGateway(settings), mode);
            default:
                return new QbConnection(new UnconfiguredQbGateway(), QbConnectionMode.Unavailable);
        }
    }

    [SupportedOSPlatform("windows")]
    private static QbGateway CreateSdkGateway(AppSettings settings) => new(new QbGatewayOptions
    {
        AppName = settings.QuickBooks.AppName,
        CompanyFile = settings.Company.FilePath,
    });
}
