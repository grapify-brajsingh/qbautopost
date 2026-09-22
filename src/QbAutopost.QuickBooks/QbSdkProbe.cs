using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.QuickBooks;

/// <summary>
/// FR-A-3 over COM: is the Desktop SDK usable at all? Registration → create the request processor →
/// <c>OpenConnection2</c> → <c>CloseConnection</c>, plus the bitness of any running QuickBooks.
/// <para>
/// It deliberately stops short of <c>BeginSession</c>: no company file is named, nothing is locked and no certificate
/// dialog is raised, so this still answers when QuickBooks is closed or busy — which is exactly when an operator asks.
/// </para>
/// <para>
/// **Not covered by an automated test** (CLAUDE.md rule 1: no test may reach a real SDK). Verified on the server —
/// tracker T-903.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QbSdkProbe(string qbXmlVersion) : IQbSdkProbe
{
    /// <summary>The QuickBooks Desktop application, whose bitness the host process must match.</summary>
    private static readonly string[] QuickBooksProcessNames = ["QBW", "QBW32"];

    public Task<QbSdkInfo> ProbeAsync(CancellationToken ct)
    {
        // The SDK is apartment-threaded, as in QbGateway: its own STA thread, never a thread-pool thread.
        var work = new TaskCompletionSource<QbSdkInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work.TrySetResult(Probe());
            }
            catch (Exception ex)
            {
                work.TrySetResult(QbSdkInfo.Unavailable(Bitness, qbXmlVersion, $"the SDK probe failed: {ex.Message}"));
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return work.Task.WaitAsync(ct);
    }

    private QbSdkInfo Probe()
    {
        var quickBooks = FindQuickBooks();
        var type = Type.GetTypeFromProgID(QbSession.ProgId, throwOnError: false);
        if (type is null)
        {
            return Answer(
                new QbSdkRequestProcessor(false, QbSession.ProgId, null, false),
                quickBooks,
                $"{QbSession.ProgId} is not registered for this {Bitness} process; install the QuickBooks SDK, or run the app with the bitness of QuickBooks");
        }

        var clsid = type.GUID.ToString("B");
        object? processor = null;
        try
        {
            processor = Activator.CreateInstance(type);
            if (processor is null)
            {
                return Answer(new QbSdkRequestProcessor(true, QbSession.ProgId, clsid, false), quickBooks, $"{QbSession.ProgId} could not be created");
            }

            // localQBD, as QbSession uses. This proves the SDK answers; it opens no company file.
            Invoke(processor, "OpenConnection2", string.Empty, "QbAutopost health check", QbSession.LocalQbd);
            try
            {
                return Answer(new QbSdkRequestProcessor(true, QbSession.ProgId, clsid, true), quickBooks, "QuickBooks Desktop SDK ready");
            }
            finally
            {
                TryClose(processor);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or MissingMethodException or TargetInvocationException or UnauthorizedAccessException)
        {
            return Answer(
                new QbSdkRequestProcessor(true, QbSession.ProgId, clsid, false),
                quickBooks,
                $"{QbSession.ProgId} is registered but the connection could not be opened: {ex.Message}");
        }
        finally
        {
            if (processor is not null && Marshal.IsComObject(processor))
            {
                Marshal.FinalReleaseComObject(processor);
            }
        }
    }

    /// <summary>
    /// Bitness is the one mismatch that fails every later call with a confusing message (T-609), so it decides
    /// <c>ok</c>. A QuickBooks that is not running cannot be compared, and that is not held against the SDK.
    /// </summary>
    private QbSdkInfo Answer(QbSdkRequestProcessor processor, QbSdkProcess quickBooks, string message)
    {
        var mismatch = quickBooks.Bitness is { } qb && !string.Equals(qb, Bitness, StringComparison.Ordinal);
        var ok = processor is { Registered: true, CanOpenConnection: true } && !mismatch;
        return new QbSdkInfo(
            ok,
            processor,
            Bitness,
            quickBooks,
            !mismatch,
            qbXmlVersion,
            mismatch ? $"the process is {Bitness} but QuickBooks is {quickBooks.Bitness}; they must match" : message);
    }

    private static QbSdkProcess FindQuickBooks()
    {
        foreach (var name in QuickBooksProcessNames)
        {
            Process[] found;
            try
            {
                found = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                continue;
            }

            try
            {
                if (found.Length == 0)
                {
                    continue;
                }

                string? path = null;
                try
                {
                    path = found[0].MainModule?.FileName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Reading another session's module needs rights the app may not have; the name alone still helps.
                }

                return new QbSdkProcess(true, $"{name}.EXE", PeBitness.Read(path));
            }
            finally
            {
                foreach (var process in found)
                {
                    process.Dispose();
                }
            }
        }

        return new QbSdkProcess(false, null, null);
    }

    private static void TryClose(object processor)
    {
        try
        {
            Invoke(processor, "CloseConnection");
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException)
        {
            // Nothing was opened against a company file; a failed close must not turn a health answer into an error.
        }
    }

    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, binder: null, target: target, args: args);

    private static string Bitness => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
}
