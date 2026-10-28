using System.Reflection;
using QbAutopost.Api.Endpoints;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-917: T-916's rule, applied to the request bodies the endpoints declare — every field a caller can send must be
/// read by production code. It lives here rather than beside <c>DecorativeSurfaceTests</c> because these records are
/// nested in the Api assembly, which <c>Core.Tests</c> does not reference.
/// <para>
/// Extending the guard this far is what found the second one. <c>CompanyFileRequest.RequireBackup</c> appears in the
/// spec's own example request for FR-A-5 and in <c>openapi.json</c>, and was read by nothing at all — the same shape
/// of defect as T-915's <c>allowModelAccounts</c>, one file away, for the same reason: a field that does nothing has
/// no behaviour for a behavioural suite to miss.
/// </para>
/// </summary>
public sealed class EndpointSurfaceTests
{
    public static TheoryData<string> RequestTypes() =>
    [
        nameof(QuickBooksEndpoints.CompanyFileRequest),
        nameof(QuickBooksEndpoints.ConnectionTestRequest),
        nameof(JobEndpoints.CreateJobRequest),
    ];

    [Theory]
    [MemberData(nameof(RequestTypes))]
    public void Should_ReadEveryField_When_ACallerCanSendIt(string typeName)
    {
        var type = RequestType(typeName);

        // Unlike the Core DTOs, these records are consumed by the very file that declares them, so nothing is
        // excluded. The declaration cannot match itself: a positional record writes `bool? RequireBackup`, with no
        // leading dot, while every read is `request?.RequireBackup`.
        var sources = ProductionSources();

        var unread = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !sources.Any(text => text.Contains("." + name, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(unread);
    }

    /// <summary>
    /// T-1009 (saved-requests spec 6, 11.2, 11.3, 11.4): every new route, with the scope the spec gives it.
    /// <c>null</c> means "no credential" (login and refresh).
    /// </summary>
    public static TheoryData<string, string, string?> NewRoutes() => new()
    {
        { "POST", "/saved-requests", ApiScopes.RequestsSubmit },
        { "GET", "/saved-requests", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/stats", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/SR-000012", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12/original", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12/versions", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12/versions/3", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12/attempts", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12/attempts/7", ApiScopes.RequestsRead },
        { "GET", "/saved-requests/12/events", ApiScopes.RequestsRead },
        { "PUT", "/saved-requests/12", ApiScopes.RequestsEdit },
        { "POST", "/saved-requests/12/validate", ApiScopes.RequestsEdit },
        { "POST", "/saved-requests/12/discard", ApiScopes.RequestsEdit },
        { "POST", "/saved-requests/12/post", ApiScopes.RequestsPost },
        { "POST", "/saved-requests/12/restore", ApiScopes.RequestsAdmin },
        { "POST", "/saved-requests/12/resolve", ApiScopes.RequestsAdmin },
        { "POST", "/auth/login", null },
        { "POST", "/auth/refresh", null },
        { "POST", "/auth/logout", ApiScopes.SignedIn },
        { "GET", "/auth/me", ApiScopes.SignedIn },
        { "POST", "/auth/change-password", ApiScopes.SignedIn },
        { "GET", "/users", ApiScopes.UsersManage },
        { "POST", "/users", ApiScopes.UsersManage },
        { "GET", "/users/u1", ApiScopes.UsersManage },
        { "PATCH", "/users/u1", ApiScopes.UsersManage },
        { "POST", "/users/u1/reset-password", ApiScopes.UsersManage },
        { "POST", "/users/u1/unlock", ApiScopes.UsersManage },
        { "GET", "/users/u1/audit", ApiScopes.UsersManage },
        { "GET", "/quickbooks/lists", ApiScopes.ListsRead },
        { "POST", "/quickbooks/lists/sync", ApiScopes.QbRead },
        { "GET", "/health/database", ApiScopes.AnyOf(ApiScopes.RequestsRead, ApiScopes.HealthRead) },
    };

    /// <summary>
    /// The validate-and-post "security trap", repeated for every new route: a route that falls through lands on
    /// <c>admin</c> (or, under <c>/quickbooks/</c>, on <c>qb:read</c>), and either answer would pass every other test.
    /// So each is pinned on both surfaces, and none may be <c>admin</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(NewRoutes))]
    public void Should_MapEveryNewRouteExplicitly(string method, string path, string? expected)
    {
        Assert.Equal(expected, ApiRoutes.ScopeFor(method, path));
        Assert.Equal(expected, ApiRoutes.ScopeFor(method, ApiRoutes.V1Prefix + path));
        Assert.NotEqual(ApiScopes.Admin, ApiRoutes.ScopeFor(method, path));
    }

    [Theory]
    [InlineData("DELETE", "/saved-requests/12")]
    [InlineData("POST", "/saved-requests/12/approve")]
    [InlineData("PUT", "/saved-requests/stats")]
    [InlineData("GET", "/auth/login")]
    [InlineData("DELETE", "/auth/me")]
    [InlineData("POST", "/auth/anything")]
    public void Should_CloseTheRoute_When_ANewPrefixHasAnUnplannedRoute(string method, string path) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor(method, path));

    [Theory]
    [InlineData("/saved-requestsx")]
    [InlineData("/usersx")]
    public void Should_NotBorrowANewScope_When_APathOnlyStartsLikeANewRoute(string path) =>
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor("GET", path));

    private static Type RequestType(string name) =>
        typeof(QuickBooksEndpoints).GetNestedType(name, BindingFlags.Public)
        ?? typeof(JobEndpoints).GetNestedType(name, BindingFlags.Public)
        ?? throw new InvalidOperationException($"No public nested request record named {name}.");


    private static List<string> ProductionSources()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        return Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(File.ReadAllText)
            .ToList();
    }

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QbAutopost.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("QbAutopost.sln was not found above " + AppContext.BaseDirectory);
    }
}
