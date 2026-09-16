using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Gateway;

namespace QbAutopost.Core.Tests.Gateway;

/// <summary>FR-11: one QuickBooks call at a time, abandoned after the busy timeout.</summary>
public sealed class ResilientQbGatewayTests
{
    private const string AddRequest = "<?xml version=\"1.0\"?><?qbxml version=\"13.0\"?><QBXML><QBXMLMsgsRq onError=\"continueOnError\"><CheckAddRq requestID=\"1\" /></QBXMLMsgsRq></QBXML>";

    private static readonly QbGatewayPolicy FastPolicy = new()
    {
        BusyTimeout = TimeSpan.FromMilliseconds(200),
        RetryDelay = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Should_ReturnInnerResponse_When_CallFinishesInTime()
    {
        var inner = new ScriptedGateway { Respond = (_, _) => Task.FromResult("<ok/>") };

        var response = await new ResilientQbGateway(inner, FastPolicy).ProcessAsync(AddRequest, CancellationToken.None);

        Assert.Equal("<ok/>", response);
    }

    [Fact]
    public async Task Should_ThrowBusy_When_CallExceedsBusyTimeout()
    {
        var inner = new ScriptedGateway { Respond = (_, _) => new TaskCompletionSource<string>().Task };

        await Assert.ThrowsAsync<QuickBooksBusyException>(
            () => new ResilientQbGateway(inner, FastPolicy).ProcessAsync(AddRequest, CancellationToken.None));
    }

    [Fact]
    public async Task Should_RefuseNextCallAsUnavailable_When_AbandonedCallIsStillRunning()
    {
        var hung = new TaskCompletionSource<string>();
        var calls = 0;
        var inner = new ScriptedGateway { Respond = (_, _) => ++calls == 1 ? hung.Task : Task.FromResult("<ok/>") };
        var gateway = new ResilientQbGateway(inner, FastPolicy);
        await Assert.ThrowsAsync<QuickBooksBusyException>(() => gateway.ProcessAsync(AddRequest, CancellationToken.None));

        await Assert.ThrowsAsync<QuickBooksUnavailableException>(() => gateway.ProcessAsync(AddRequest, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Should_AcceptNextCall_When_AbandonedCallHasFinished()
    {
        var hung = new TaskCompletionSource<string>();
        var calls = 0;
        var inner = new ScriptedGateway { Respond = (_, _) => ++calls == 1 ? hung.Task : Task.FromResult("<ok/>") };
        var gateway = new ResilientQbGateway(inner, FastPolicy);
        await Assert.ThrowsAsync<QuickBooksBusyException>(() => gateway.ProcessAsync(AddRequest, CancellationToken.None));

        hung.SetResult("<late/>");
        var response = await gateway.ProcessAsync(AddRequest, CancellationToken.None);

        Assert.Equal("<ok/>", response);
    }

    [Fact]
    public async Task Should_RunOneCallAtATime_When_CallsOverlap()
    {
        var running = 0;
        var maxRunning = 0;
        var inner = new ScriptedGateway
        {
            Respond = async (_, _) =>
            {
                var now = Interlocked.Increment(ref running);
                maxRunning = Math.Max(maxRunning, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
                return "<ok/>";
            },
        };
        var gateway = new ResilientQbGateway(inner, FastPolicy with { BusyTimeout = TimeSpan.FromSeconds(10) });

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => gateway.ProcessAsync(AddRequest, CancellationToken.None)));

        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task Should_PassThroughOtherErrors_When_InnerCallFails()
    {
        var inner = new ScriptedGateway { Respond = (_, _) => throw new InvalidOperationException("boom") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ResilientQbGateway(inner, FastPolicy).ProcessAsync(AddRequest, CancellationToken.None));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Should_ThrowCancelled_When_CallerCancels()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedGateway { Respond = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => "", TaskScheduler.Default) };
        var call = new ResilientQbGateway(inner, FastPolicy with { BusyTimeout = TimeSpan.FromSeconds(10) })
            .ProcessAsync(AddRequest, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task Should_ApplyBusyTimeout_When_AskingForCompanyFile()
    {
        var inner = new ScriptedGateway { CompanyFile = _ => new TaskCompletionSource<string>().Task };

        await Assert.ThrowsAsync<QuickBooksBusyException>(
            () => new ResilientQbGateway(inner, FastPolicy).CurrentCompanyFileAsync(CancellationToken.None));
    }

    internal sealed class ScriptedGateway : IQbGateway
    {
        public Func<string, CancellationToken, Task<string>> Respond { get; init; } = (_, _) => Task.FromResult("");

        public Func<CancellationToken, Task<string>> CompanyFile { get; init; } = _ => Task.FromResult(@"C:\x.QBW");

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => Respond(qbxml, ct);

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => CompanyFile(ct);
    }
}
