using System.Diagnostics;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Logging;

/// <summary>
/// Logs every Hermes call (spec §14): task, input size, duration and outcome. Prompts and answers are not logged (they
/// hold statement text); their full copies are the audit files in the job's <c>output/hermes/</c> (FR-17).
/// Pings are logged by the health endpoint.
/// </summary>
public sealed class LoggingHermesClient(IHermesClient inner, ILogger<LoggingHermesClient> log) : IHermesClient
{
    public async Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable
    {
        log.LogInformation("Hermes {Task}: asking ({Characters} characters of input)", request.Task, request.UserContent.Length);
        var watch = Stopwatch.StartNew();
        try
        {
            var answer = await inner.CompleteJsonAsync<T>(request, ct);
            log.LogInformation("Hermes {Task}: valid answer in {ElapsedMs} ms", request.Task, watch.ElapsedMilliseconds);
            return answer;
        }
        catch (HermesValidationException ex)
        {
            log.LogWarning(
                "Hermes {Task}: answer rejected twice after {ElapsedMs} ms: {Errors}", request.Task, watch.ElapsedMilliseconds, string.Join("; ", ex.Errors));
            throw;
        }
        catch (HermesUnavailableException ex)
        {
            log.LogWarning("Hermes {Task}: no answer after {ElapsedMs} ms: {Reason}", request.Task, watch.ElapsedMilliseconds, ex.Reason);
            throw;
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("Hermes {Task}: cancelled after {ElapsedMs} ms", request.Task, watch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Hermes {Task}: failed after {ElapsedMs} ms", request.Task, watch.ElapsedMilliseconds);
            throw;
        }
    }

    public Task<HermesPing> PingAsync(CancellationToken ct) => inner.PingAsync(ct);
}
