using Microsoft.Extensions.Logging;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Api.Tests.Logging;

public sealed class LoggingHermesClientTests
{
    private const string Secret = "STATEMENT TEXT 12345";

    private readonly ListLogger<LoggingHermesClient> _log = new();
    private readonly FakeHermesClient _hermes = new();

    private static HermesRequest Request => new(HermesTask.Spec, "system", "user " + Secret);

    private Task<SpecAnswer> AskAsync() =>
        new LoggingHermesClient(_hermes, _log).CompleteJsonAsync<SpecAnswer>(Request, CancellationToken.None);

    [Fact]
    public async Task Should_LogTaskAndDuration_When_AnswerIsValid()
    {
        await AskAsync();

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Hermes Spec", StringComparison.Ordinal) && e.Message.Contains(" ms", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LogWarningAndRethrow_When_HermesIsUnavailable()
    {
        _hermes.Respond(_ => throw new HermesUnavailableException(HermesTask.Spec, "connection refused"));

        await Assert.ThrowsAsync<HermesUnavailableException>(AskAsync);

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("connection refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LogWarningWithErrors_When_AnswerFailsValidation()
    {
        _hermes.Respond(_ => throw new HermesValidationException(HermesTask.Spec, ["kinds is empty"]));

        await Assert.ThrowsAsync<HermesValidationException>(AskAsync);

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("kinds is empty", StringComparison.Ordinal));
    }
}
