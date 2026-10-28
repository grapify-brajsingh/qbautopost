namespace QbAutopost.Api.Configuration;

/// <summary>
/// T-1116 (dashboard v2 spec 13.1, R-2): the <c>Dashboard</c> section. The built dashboard is served from
/// <see cref="Path"/> on every path that is not the API's (<c>ApiRoutes.IsDashboardPath</c>).
/// </summary>
public sealed class DashboardSettings
{
    /// <summary>The folder holding the dashboard's built files (<c>index.html</c>, <c>assets/</c>).</summary>
    public string Path { get; set; } = @"C:\qb-autopost\dashboard";

    /// <summary>False leaves the pipeline exactly as it was before the dashboard was served.</summary>
    public bool Enabled { get; set; } = true;
}
