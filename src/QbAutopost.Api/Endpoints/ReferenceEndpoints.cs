using QbAutopost.Api.Configuration;
using Scalar.AspNetCore;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// T-913 (FR-A-18): the API reference — two separate things on purpose.
/// <para>
/// The <b>document</b> (<c>GET /api/v1/openapi.json</c>) is hand-authored and checked in at
/// <c>wwwroot/openapi.json</c>: .NET 8 has no built-in generator (<c>AddOpenApi</c> is .NET 9 and
/// <c>global.json</c> pins 8.0.x) and Swashbuckle is excluded by the owner. It is kept honest by
/// <c>OpenApiDriftTests</c>, which compares it with <see cref="EndpointDataSource"/> rather than trusting it.
/// </para>
/// <para>
/// The <b>UI</b> (<c>GET /api/v1/reference</c>) is Scalar rendering that document. Both are mapped only when
/// <c>Api:Reference:Enabled</c> is true, so on an installation that posts money the explorer does not exist at all
/// rather than existing behind a scope.
/// </para>
/// </summary>
public static class ReferenceEndpoints
{
    /// <summary>The document as it sits beside the executable, named once so the map and the copy rule agree.</summary>
    public const string DocumentFile = "openapi.json";

    /// <summary>
    /// Where <c>Api:Reference:UseCdn</c> points the page when an operator explicitly asks for the public bundle.
    /// Nothing reaches it by default; it is here so the setting has one meaning rather than a configurable URL that
    /// could be pointed anywhere. (Scalar's own <c>CdnUrl</c> property is obsolete in 2.13; <c>BundleUrl</c> is the
    /// one it kept, and it does the same thing.)
    /// </summary>
    public const string CdnBundleUrl = "https://cdn.jsdelivr.net/npm/@scalar/api-reference";

    public static IEndpointRouteBuilder MapReferenceEndpoints(
        this IEndpointRouteBuilder routes, ReferenceSettings settings, IWebHostEnvironment environment)
    {
        routes.MapGet(ApiRoutes.OpenApiPath, () =>
        {
            var file = FindDocument(environment);
            return file is null
                ? Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "The API document is missing",
                    detail: $"{DocumentFile} was not found beside the application; the installation is incomplete.")
                : Results.File(file, "application/json");
        });

        routes.MapScalarApiReference(ApiRoutes.ReferencePath, options => Configure(options, settings));
        return routes;
    }

    /// <summary>
    /// The page's settings, as a pure function so the three that matter can be asserted without a browser.
    /// <para>
    /// <c>Telemetry</c> and <c>DefaultFonts</c> are off unconditionally: both reach a third party from the reader's
    /// browser, neither is what <c>Api:Reference:UseCdn</c> is about, and an operator turning that setting on is
    /// asking for the script bundle, not for a font request and a usage ping.
    /// </para>
    /// </summary>
    public static void Configure(ScalarOptions options, ReferenceSettings settings)
    {
        options.Title = "QbAutopost API v1";
        options.OpenApiRoutePattern = ApiRoutes.V1Prefix + ApiRoutes.OpenApiPath;

        // FR-A-18: a reader of the documentation must not be one click away from posting a live transaction.
        options.HideTestRequestButton = !settings.AllowTryIt;
        options.HideClientButton = !settings.AllowTryIt;

        options.Telemetry = false;
        options.DefaultFonts = false;
        if (settings.UseCdn)
        {
            options.BundleUrl = CdnBundleUrl;
        }
    }

    /// <summary>
    /// The document beside the executable, then beside the content root. Both are looked at because the two are the
    /// same folder on the server (api-v1 §12 sets the content root to <c>AppContext.BaseDirectory</c>) and different
    /// ones under <c>WebApplicationFactory</c>, which keeps its own content root at the project.
    /// </summary>
    private static string? FindDocument(IWebHostEnvironment environment)
    {
        string?[] roots =
        [
            environment.WebRootPath,
            Path.Combine(environment.ContentRootPath, "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        ];

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root!, DocumentFile))
            .FirstOrDefault(File.Exists);
    }
}
