namespace QbAutopost.Api.Configuration;

/// <summary>
/// T-901 (api-v1 §12): which folder the host reads <c>appsettings.json</c> from.
/// <para>
/// Session-13 server defect 1: started from <c>C:\Users\…</c> instead of the app folder, the published exe found no
/// <c>appsettings.json</c> at all and ran with an empty <c>Company:Name</c> and Hermes enabled — it would have posted
/// against the wrong configuration. The content root is therefore the executable's own folder, not the working
/// directory.
/// </para>
/// </summary>
public static class ContentRoot
{
    /// <summary>The test host sets its own content root; overriding it would break <c>WebApplicationFactory</c>.</summary>
    public const string TestingEnvironment = "Testing";

    /// <summary>
    /// The content root to use, or <c>null</c> to keep the host default (the working directory).
    /// </summary>
    /// <param name="environmentName">`ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT`, read before the host is built.</param>
    /// <param name="baseDirectory">Normally <see cref="AppContext.BaseDirectory"/>.</param>
    public static string? Select(string? environmentName, string? baseDirectory) =>
        string.IsNullOrWhiteSpace(baseDirectory)
        || string.Equals(environmentName, TestingEnvironment, StringComparison.OrdinalIgnoreCase)
            ? null
            : baseDirectory;
}
