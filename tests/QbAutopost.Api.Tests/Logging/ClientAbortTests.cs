using Microsoft.AspNetCore.Http;
using QbAutopost.Api.Logging;
using Serilog.Events;

namespace QbAutopost.Api.Tests.Logging;

/// <summary>
/// T-1304 (dashboard-integration-m13 §4a): the dashboard cancels reads it no longer needs (a filter change, leaving a
/// page). The cancellation reaches EF through <c>RequestAborted</c>; that is the client leaving, not a server fault, so
/// it is answered 499 and logged at Information instead of a 500 at Error.
/// </summary>
public sealed class ClientAbortTests
{
    private static DefaultHttpContext AbortedContext()
    {
        var context = new DefaultHttpContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        context.RequestAborted = cts.Token;
        return context;
    }

    [Fact]
    public async Task Should_Answer499_When_ClientAbortedRequest()
    {
        var context = AbortedContext();
        var middleware = new ClientAbortMiddleware(ctx => throw new OperationCanceledException(ctx.RequestAborted));

        await middleware.InvokeAsync(context);

        Assert.Equal(ClientAbortMiddleware.ClientClosedRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task Should_Rethrow_When_CancelledWithoutClientAbort()
    {
        var context = new DefaultHttpContext();
        var middleware = new ClientAbortMiddleware(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task Should_Rethrow_When_OtherExceptionAfterClientAbort()
    {
        var context = AbortedContext();
        var middleware = new ClientAbortMiddleware(_ => throw new InvalidOperationException("a real fault"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public void Should_LogInformation_When_ClientClosedRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v2/audit-events";
        context.Response.StatusCode = ClientAbortMiddleware.ClientClosedRequest;

        Assert.Equal(LogEventLevel.Information, RequestLog.Level(context, 10, null));
    }
}
