using System.Diagnostics;
using System.Globalization;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// Logs every call to QuickBooks (spec §14): what is sent (request names and count, never the body), how long it took,
/// the status of every response, and failures. It sits inside <c>ResilientQbGateway</c>, so a retried call shows as
/// two calls and a call that outlives the busy timeout is still logged when it really ends.
/// </summary>
public sealed class LoggingQbGateway(IQbGateway inner, ILogger<LoggingQbGateway> log, TimeSpan busyTimeout) : IQbGateway
{
    private int _calls;

    public IQbGateway Inner => inner;

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
        LogProcessAsync(qbxml, target: null, token => inner.ProcessAsync(qbxml, token), ct);

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        LogCompanyFileAsync(target: null, inner.CurrentCompanyFileAsync, ct);

    /// <summary>M17 §4.7: as the untargeted call, with the company file named on the "sending" line.</summary>
    public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) =>
        LogProcessAsync(qbxml, target, token => inner.ProcessAsync(target, qbxml, token), ct);

    /// <inheritdoc cref="ProcessAsync(QbTarget, string, CancellationToken)"/>
    public Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) =>
        LogCompanyFileAsync(target, token => inner.CurrentCompanyFileAsync(target, token), ct);

    private async Task<string> LogProcessAsync(string qbxml, QbTarget? target, Func<CancellationToken, Task<string>> send, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        var request = QbXmlLogSummary.Read(qbxml);
        var requests = request is null ? "an unreadable message set" : QbXmlLogSummary.Describe(request);
        if (target is null)
        {
            log.LogInformation("QuickBooks call {CallNo}: sending {Requests} ({Length} characters)", call, requests, qbxml.Length);
        }
        else
        {
            log.LogInformation(
                "QuickBooks call {CallNo}: sending {Requests} to {CompanyFile} ({Length} characters)",
                call, requests, target.CompanyFile, qbxml.Length);
        }

        var watch = Stopwatch.StartNew();
        string response;
        try
        {
            response = await send(ct);
        }
        catch (Exception ex)
        {
            LogFailure(call, watch.Elapsed, ex);
            throw;
        }

        LogAnswer(call, watch.Elapsed, response);
        WarnIfPastBusyTimeout(call, watch.Elapsed);
        return response;
    }

    private async Task<string> LogCompanyFileAsync(QbTarget? target, Func<CancellationToken, Task<string>> ask, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        if (target is null)
        {
            log.LogInformation("QuickBooks call {CallNo}: asking which company file is open", call);
        }
        else
        {
            log.LogInformation("QuickBooks call {CallNo}: asking which company file is open, session on {CompanyFile}", call, target.CompanyFile);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var file = await ask(ct);
            log.LogInformation("QuickBooks call {CallNo}: company file is {CompanyFile} ({ElapsedMs} ms)", call, file, watch.ElapsedMilliseconds);
            WarnIfPastBusyTimeout(call, watch.Elapsed);
            return file;
        }
        catch (Exception ex)
        {
            LogFailure(call, watch.Elapsed, ex);
            throw;
        }
    }

    private void LogAnswer(int call, TimeSpan elapsed, string response)
    {
        var messages = QbXmlLogSummary.Read(response);
        if (messages is null)
        {
            log.LogWarning(
                "QuickBooks call {CallNo}: answered in {ElapsedMs} ms, but the answer is not readable qbXML ({Length} characters)",
                call, (long)elapsed.TotalMilliseconds, response.Length);
            return;
        }

        var problems = messages.Where(IsProblem).ToList();
        log.LogInformation(
            "QuickBooks call {CallNo}: answered in {ElapsedMs} ms with {Count} responses: {Ok} ok, {Problems} with errors or warnings",
            call, (long)elapsed.TotalMilliseconds, messages.Count, messages.Count - problems.Count, problems.Count);

        foreach (var m in messages)
        {
            if (IsProblem(m))
            {
                log.LogWarning(
                    "QuickBooks call {CallNo}: {Response} {RequestId} status {StatusCode} ({Severity}): {StatusMessage}",
                    call, m.Name, m.RequestId, m.StatusCode, m.Severity, m.StatusMessage);
            }
            else
            {
                log.LogDebug(
                    "QuickBooks call {CallNo}: {Response} {RequestId} status {StatusCode} ({Severity}) TxnID {TxnId}, {Returned} returned: {StatusMessage}",
                    call, m.Name, m.RequestId, m.StatusCode, m.Severity, m.TxnId, m.Returned, m.StatusMessage);
            }
        }
    }

    private void LogFailure(int call, TimeSpan elapsed, Exception ex)
    {
        var ms = (long)elapsed.TotalMilliseconds;
        switch (ex)
        {
            case QuickBooksUnavailableException:
                log.LogWarning("QuickBooks call {CallNo}: nothing sent, QuickBooks not reachable after {ElapsedMs} ms: {Error}", call, ms, ex.Message);
                break;
            case QuickBooksCallException com:
                log.LogError(
                    ex,
                    "QuickBooks call {CallNo}: failed after {ElapsedMs} ms with {ErrorCode}: {Error}; the request may have been applied",
                    call, ms, string.Create(CultureInfo.InvariantCulture, $"0x{com.ErrorCode:X8}"), ex.Message);
                break;
            case OperationCanceledException:
                log.LogInformation("QuickBooks call {CallNo}: cancelled after {ElapsedMs} ms", call, ms);
                break;
            default:
                log.LogError(ex, "QuickBooks call {CallNo}: failed after {ElapsedMs} ms: {Error}", call, ms, ex.Message);
                break;
        }

        WarnIfPastBusyTimeout(call, elapsed);
    }

    private void WarnIfPastBusyTimeout(int call, TimeSpan elapsed)
    {
        if (elapsed > busyTimeout)
        {
            log.LogWarning(
                "QuickBooks call {CallNo}: took {ElapsedSeconds} s, longer than the busy timeout ({BusyTimeoutSeconds} s); the caller had already given up (quickbooks-busy), check QuickBooks before re-posting",
                call, Math.Round(elapsed.TotalSeconds, 1), busyTimeout.TotalSeconds);
        }
    }

    private static bool IsProblem(QbXmlLogMessage m) =>
        m.Severity is not null && !m.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase);
}
