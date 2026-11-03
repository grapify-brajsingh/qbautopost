namespace QbAutopost.Api.Logging;

/// <summary>
/// T-1304 (dashboard-integration-m13 §4a): a request the client abandoned (the dashboard cancels reads it no longer
/// needs) ends in an <see cref="OperationCanceledException"/> from <c>RequestAborted</c>. That is not a server fault: it
/// is answered <see cref="ClientClosedRequest"/> (nginx's 499) and the request line is written at Information
/// (<see cref="RequestLog.Level"/>), not as a 500 at Error. Any other exception, or a cancellation the client did not
/// cause, still reaches the exception handler.
/// <para>
/// Sits inside the request log, so the one request line records the 499. Posting is unaffected: the poster runs its
/// claim and recording under <c>CancellationToken.None</c> (saved-requests-dashboard-v2 §13.2).
/// </para>
/// </summary>
public sealed class ClientAbortMiddleware(RequestDelegate next)
{
    public const int ClientClosedRequest = 499;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        }
    }
}
