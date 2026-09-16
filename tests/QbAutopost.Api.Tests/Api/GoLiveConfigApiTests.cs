using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// Plan T-804 go-live switch: <c>DryRunDefault</c> decides what a job that does not say <c>dryRun</c> does (spec §6, §12),
/// and the program ships with dry run on, so a new install never posts by accident.
/// </summary>
public sealed class GoLiveConfigApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private WebApplicationFactory<Program>? _host;

    public void Dispose()
    {
        _host?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void Should_ShipWithDryRunDefaultTrue_When_ReadingPublishedAppsettings()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.True(doc.RootElement.GetProperty("DryRunDefault").GetBoolean());
    }

    [Fact]
    public async Task Should_StopAtReadyWithoutWrites_When_DryRunIsOmittedAndDefaultIsTrue()
    {
        using var client = Client(dryRunDefault: "true");
        var folder = _factory.Dir.CopySampleJob();

        var view = await SubmitWithoutDryRunAsync(client, folder);

        Assert.Equal((JobStatus.Ready, true, 0), (view.Status, view.DryRun, view.Counts.Posted));
        Assert.Empty(_factory.Gateway.Writes);
    }

    [Fact]
    public async Task Should_PostImmediately_When_DryRunIsOmittedAndDefaultIsFalse()
    {
        using var client = Client(dryRunDefault: "false");
        var folder = _factory.Dir.CopySampleJob();

        var view = await SubmitWithoutDryRunAsync(client, folder);

        Assert.False(view.DryRun);
        Assert.Equal(8, view.Counts.Posted);
        Assert.NotEmpty(_factory.Gateway.Writes);
    }

    [Fact]
    public async Task Should_StayDryRun_When_RequestSaysDryRunAndDefaultIsFalse()
    {
        using var client = Client(dryRunDefault: "false");
        var folder = _factory.Dir.CopySampleJob();

        var view = await client.RunToEndAsync(folder, dryRun: true);

        Assert.Equal((JobStatus.Ready, true), (view.Status, view.DryRun));
        Assert.Empty(_factory.Gateway.Writes);
    }

    /// <summary>Sends <c>{ folder }</c> with <c>dryRun</c> null, so the host's default decides.</summary>
    private static async Task<JobView> SubmitWithoutDryRunAsync(HttpClient client, string folder)
    {
        using var response = await client.PostJobAsync(folder);
        response.EnsureSuccessStatusCode();
        return await client.WaitForJobAsync("2026-08-tropicana");
    }

    private HttpClient Client(string dryRunDefault)
    {
        _host = _factory.WithSetting("DryRunDefault", dryRunDefault);
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }
}
