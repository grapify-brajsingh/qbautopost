using System.Diagnostics;
using System.Runtime.Versioning;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.QuickBooks;

/// <summary>
/// M14 §6: is QuickBooks already running in the API's own Windows session? <c>BeginSession</c> starts QuickBooks when
/// it is not running, so a scheduled sync asks this first and never starts it. Looks at process names only; it never
/// calls the SDK and cannot tell which company file is open.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QbPresence : IQbPresence
{
    private static readonly string[] QuickBooksProcessNames = ["QBW", "QBW32"];

    private readonly Func<IEnumerable<(string Name, int SessionId)>> _lookup;
    private readonly int _currentSessionId;

    public QbPresence() : this(ListProcesses, CurrentSessionId()) { }

    /// <summary>The process list and own session are injected so the name/session logic is testable without processes.</summary>
    public QbPresence(Func<IEnumerable<(string Name, int SessionId)>> lookup, int currentSessionId)
    {
        _lookup = lookup;
        _currentSessionId = currentSessionId;
    }

    public bool IsCompanyOpen()
    {
        try
        {
            foreach (var (name, sessionId) in _lookup())
            {
                if (sessionId == _currentSessionId
                    && QuickBooksProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable process list: treat as "not open", so nothing is ever started or called on a guess.
        }

        return false;
    }

    private static int CurrentSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    private static IEnumerable<(string Name, int SessionId)> ListProcesses()
    {
        foreach (var name in QuickBooksProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    int sessionId;
                    try
                    {
                        sessionId = process.SessionId;
                    }
                    catch (Exception)
                    {
                        continue; // access denied or exited meanwhile
                    }

                    yield return (name, sessionId);
                }
            }
        }
    }
}
