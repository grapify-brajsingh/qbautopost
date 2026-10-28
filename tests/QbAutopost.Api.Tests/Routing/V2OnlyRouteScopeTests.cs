using System.Net;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Routing;

/// <summary>
/// T-1114 (O-1, dashboard v2 spec 13.1): the audit feed exists under <c>/api/v2</c> only. Its v1 and flat forms are
/// unmapped paths and keep the <c>admin</c> fall-through, so a viewer is refused (403), not told 404.
/// </summary>
public sealed class V2OnlyRouteScopeTests : IDisposable
{
    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData("/api/v1/audit-events")]
    [InlineData("/api/v1/audit-events.csv")]
    [InlineData("/api/V1/Audit-Events")]
    public void Should_KeepAdminScope_When_V1AuditEventsPathCalled(string path) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor("GET", path));

    [Theory]
    [InlineData("/audit-events")]
    [InlineData("/audit-events.csv")]
    public void Should_KeepAdminScope_When_FlatAuditEventsPathCalled(string path) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor("GET", path));

    [Theory]
    [InlineData("/api/v2/audit-events")]
    [InlineData("/api/v2/audit-events.csv")]
    [InlineData("/API/V2/Audit-Events")]
    public void Should_RequireRequestsRead_When_V2AuditEventsPathCalled(string path)
    {
        Assert.Equal(ApiScopes.RequestsRead, ApiRoutes.ScopeFor("GET", path));
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor("POST", path));
    }

    [Theory]
    [InlineData("/api/v1/audit-events")]
    [InlineData("/api/v1/audit-events.csv")]
    [InlineData("/audit-events")]
    [InlineData("/audit-events.csv")]
    public async Task Should_Refuse403_When_ViewerCallsUnmappedAuditEventsPath(string path)
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
