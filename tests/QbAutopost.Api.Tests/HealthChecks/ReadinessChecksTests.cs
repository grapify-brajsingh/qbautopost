using QbAutopost.Api.Configuration;
using QbAutopost.Api.Health;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.HealthChecks;

/// <summary>
/// T-902 / FR-A-2: readiness is decided from local state only. An <c>error</c> check makes the host not ready; a
/// <c>warning</c> is reported but does not (a missing <c>qb-lists.json</c> only means lists were never synced —
/// jobs resolved entirely by rules still run).
/// </summary>
public sealed class ReadinessChecksTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private AppSettings Ready()
    {
        var rules = _dir.Combine("rules.json");
        File.WriteAllText(rules, "{}");
        var lists = _dir.Combine("qb-lists.json");
        File.WriteAllText(lists, "{}");
        return new AppSettings
        {
            Company = { Name = "Tropicana Properties LLC", RulesFile = rules },
            Paths = { QbLists = lists, Logs = _dir.Combine("logs") },
        };
    }

    private static ReadinessCheck Check(ReadinessView view, string name) =>
        Assert.Single(view.Checks, c => c.Name == name);

    [Fact]
    public void Should_BeReady_When_EveryCheckPasses()
    {
        var view = Readiness.Evaluate(Ready(), workerStarted: true);

        Assert.True(view.Ok);
        Assert.All(view.Checks, c => Assert.True(c.Ok));
    }

    [Fact]
    public void Should_NotBeReady_When_RulesFileIsMissing()
    {
        var settings = Ready();
        File.Delete(settings.Company.RulesFile);

        var view = Readiness.Evaluate(settings, workerStarted: true);

        Assert.False(view.Ok);
        var rules = Check(view, "rules");
        Assert.False(rules.Ok);
        Assert.Equal(Readiness.Error, rules.Severity);
    }

    [Fact]
    public void Should_NotBeReady_When_CompanyNameIsBlank()
    {
        var settings = Ready();
        settings.Company.Name = "  ";

        var view = Readiness.Evaluate(settings, workerStarted: true);

        Assert.False(view.Ok);
        Assert.False(Check(view, "settings").Ok);
    }

    [Fact]
    public void Should_NotBeReady_When_WorkerHasNotStarted()
    {
        var view = Readiness.Evaluate(Ready(), workerStarted: false);

        Assert.False(view.Ok);
        Assert.False(Check(view, "worker").Ok);
    }

    [Fact]
    public void Should_StayReady_When_QbListsIsMissing()
    {
        var settings = Ready();
        File.Delete(settings.Paths.QbLists);

        var view = Readiness.Evaluate(settings, workerStarted: true);

        Assert.True(view.Ok);
        var lists = Check(view, "qb-lists");
        Assert.False(lists.Ok);
        Assert.Equal(Readiness.Warning, lists.Severity);
    }

    [Fact]
    public void Should_NotBeReady_When_LogFolderCannotBeCreated()
    {
        var settings = Ready();
        var blocker = _dir.Combine("blocker");
        File.WriteAllText(blocker, "a file where the log folder should be");
        settings.Paths.Logs = Path.Combine(blocker, "logs");

        var view = Readiness.Evaluate(settings, workerStarted: true);

        Assert.False(view.Ok);
        Assert.False(Check(view, "logFolder").Ok);
    }

    [Fact]
    public void Should_NameEveryCheck_When_Evaluated()
    {
        var view = Readiness.Evaluate(Ready(), workerStarted: true);

        Assert.Equal(
            ["settings", "rules", "qb-lists", "logFolder", "worker"],
            view.Checks.Select(c => c.Name));
    }
}
