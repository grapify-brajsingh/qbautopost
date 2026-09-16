using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.QuickBooks;

/// <summary>
/// One QuickBooks Desktop SDK session (spec FR-11, ADR-0006): late-bound <c>QBXMLRP2.RequestProcessor</c>,
/// <c>OpenConnection2("", AppName, localQBD)</c> → <c>BeginSession(file, DoNotCare)</c> → requests →
/// <c>EndSession</c> → <c>CloseConnection</c>. Must be used from a single STA thread. The enum constants below are the
/// ones T-609 verifies on the server first (plan §5).
/// Failures before a request is sent become <see cref="QuickBooksUnavailableException"/>; a COM error from
/// <c>ProcessRequest</c> becomes <see cref="QuickBooksCallException"/> because the request may have been applied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QbSession : IDisposable
{
    public const string ProgId = "QBXMLRP2.RequestProcessor";

    /// <summary><c>QBXMLRPConnectionType.localQBD</c>.</summary>
    public const int LocalQbd = 1;

    /// <summary><c>QBFileMode.qbFileOpenDoNotCare</c>.</summary>
    public const int FileOpenDoNotCare = 2;

    private readonly object _processor;
    private bool _connected;
    private string? _ticket;

    private QbSession(object processor) => _processor = processor;

    public static QbSession Open(string appName, string companyFile)
    {
        if (string.IsNullOrWhiteSpace(companyFile))
        {
            throw new QuickBooksUnavailableException("Company:FilePath is not set");
        }

        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
            ?? throw new QuickBooksUnavailableException(
                $"{ProgId} is not registered for this {Bitness} process; install the QuickBooks SDK or run the app with the bitness of QuickBooks");

        object processor;
        try
        {
            processor = Activator.CreateInstance(type)
                ?? throw new QuickBooksUnavailableException($"{ProgId} could not be created");
        }
        catch (COMException ex)
        {
            throw new QuickBooksUnavailableException($"{ProgId} could not be created ({Describe(ex)}); check that the process bitness ({Bitness}) matches QuickBooks", ex);
        }

        var session = new QbSession(processor);
        try
        {
            session.Invoke("OpenConnection2", "", appName, LocalQbd);
            session._connected = true;
            session._ticket = (string?)session.Invoke("BeginSession", companyFile, FileOpenDoNotCare)
                ?? throw new QuickBooksUnavailableException("BeginSession returned no ticket");
            return session;
        }
        catch (COMException ex)
        {
            session.Dispose();
            throw new QuickBooksUnavailableException(
                $"could not open a QuickBooks session for '{companyFile}' ({Describe(ex)}); is QuickBooks running with the certificate accepted for {appName}?",
                ex);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static string Bitness => Environment.Is64BitProcess ? "x64" : "x86";

    /// <summary>Sends one qbXML message set and returns the raw response.</summary>
    public string ProcessRequest(string qbxml)
    {
        try
        {
            return (string?)Invoke("ProcessRequest", _ticket, qbxml) ?? "";
        }
        catch (COMException ex)
        {
            throw new QuickBooksCallException($"ProcessRequest failed ({Describe(ex)})", ex.HResult, ex);
        }
    }

    public string CurrentCompanyFile()
    {
        try
        {
            return (string?)Invoke("GetCurrentCompanyFileName", _ticket) ?? "";
        }
        catch (COMException ex)
        {
            throw new QuickBooksUnavailableException($"GetCurrentCompanyFileName failed ({Describe(ex)})", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_ticket is not null)
            {
                Invoke("EndSession", _ticket);
            }
        }
        catch (COMException)
        {
            // The request already has its answer; a failed EndSession must not turn it into an error.
        }

        try
        {
            if (_connected)
            {
                Invoke("CloseConnection");
            }
        }
        catch (COMException)
        {
            // As above.
        }

        _ticket = null;
        _connected = false;
        Marshal.FinalReleaseComObject(_processor);
    }

    private static string Describe(COMException ex) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{ex.HResult:X8}: {ex.Message}");

    private object? Invoke(string method, params object?[] args)
    {
        try
        {
            return _processor.GetType().InvokeMember(
                method, BindingFlags.InvokeMethod, binder: null, _processor, args, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
