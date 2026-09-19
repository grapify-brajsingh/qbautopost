using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Core.Tests.Hermes;

public sealed class DisabledHermesClientTests
{
    [Fact]
    public async Task Should_ThrowUnavailable_When_AskedForAnAnswer()
    {
        var client = new DisabledHermesClient();

        var ex = await Assert.ThrowsAsync<HermesUnavailableException>(() =>
            client.CompleteJsonAsync<SpecAnswer>(new HermesRequest(HermesTask.Account, "system", "user"), CancellationToken.None));

        Assert.Equal(HermesTask.Account, ex.Task);
        Assert.Contains("Hermes:Enabled", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReportNotOk_When_Pinged()
    {
        var ping = await new DisabledHermesClient().PingAsync(CancellationToken.None);

        Assert.False(ping.Ok);
        Assert.Contains("Hermes:Enabled", ping.Message, StringComparison.Ordinal);
    }
}
