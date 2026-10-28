using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;

namespace QbAutopost.Api.Hosting;

/// <summary>
/// T-1116 (dashboard v2 spec 13.1, R-2): serves the built dashboard from <c>Dashboard:Path</c> on every path
/// <see cref="ApiRoutes.IsDashboardPath"/> gives to it, and only there.
/// <para>
/// The branch sits in front of the audit, the caller resolver, the rate limiter and idempotency, and never rejoins the
/// main pipeline: a dashboard path is answered here, without authentication, and an API path never enters it, so every
/// <c>/api/v1</c>, <c>/api/v2</c> and legacy flat path keeps exactly the handling it had before (O-1).
/// </para>
/// <list type="bullet">
/// <item>An existing file is served from a <see cref="PhysicalFileProvider"/>, whose default
/// <see cref="Microsoft.Extensions.FileProviders.Physical.ExclusionFilters.Sensitive"/> hides dotfiles, hidden and
/// system files. A path with any segment starting with a dot is never looked up at all.</item>
/// <item>Any other <c>GET</c> or <c>HEAD</c> gets <c>index.html</c> (the client router reads the path); any other
/// method gets <c>405</c>.</item>
/// <item>No folder, or no <c>index.html</c> in it: <c>404 text/plain</c> "dashboard not installed", and one warning at
/// startup. The API is unaffected.</item>
/// </list>
/// </summary>
public static class DashboardHosting
{
    /// <summary>R-2.5, verbatim: what every HTML answer carries.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";

    public const string HtmlCacheControl = "no-cache";

    public const string AssetCacheControl = "public, max-age=31536000, immutable";

    public const string NotInstalledText = "dashboard not installed";

    private const string IndexFile = "index.html";

    private const string AssetsPrefix = "/assets";

    /// <summary>
    /// Adds the dashboard branch when <c>Dashboard:Enabled</c> is true; otherwise the pipeline is left exactly as it was.
    /// Must be called before the caller resolver.
    /// </summary>
    public static WebApplication UseDashboard(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<AppSettings>>().Value.Dashboard;
        if (!settings.Enabled)
        {
            app.Logger.LogInformation("Dashboard: disabled (Dashboard:Enabled is false)");
            return app;
        }

        var folder = settings.Path;
        var provider = Directory.Exists(folder) ? new PhysicalFileProvider(folder) : null;
        if (provider is null || !provider.GetFileInfo(IndexFile).Exists)
        {
            app.Logger.LogWarning(
                "Dashboard not installed: {DashboardPath} has no {IndexFile}. Dashboard paths answer 404 until it is "
                + "installed (deploy/install-dashboard.ps1); the API is unaffected",
                folder, IndexFile);
        }
        else
        {
            app.Logger.LogInformation("Dashboard: served from {DashboardPath}", folder);
        }

        app.UseWhen(c => ApiRoutes.IsDashboardPath(c.Request.Path), branch => Configure(branch, provider));
        return app;
    }

    private static void Configure(IApplicationBuilder branch, PhysicalFileProvider? provider)
    {
        if (provider is null)
        {
            branch.Run(NotInstalledAsync);
            return;
        }

        var files = new StaticFileOptions { FileProvider = provider, OnPrepareResponse = ApplyHeaders };

        // Checked per request, so an install into an existing folder, or one swapped in by rename, is picked up.
        branch.Use(async (context, next) =>
        {
            if (!provider.GetFileInfo(IndexFile).Exists)
            {
                await NotInstalledAsync(context);
                return;
            }

            await next(context);
        });
        branch.UseWhen(c => !HasDotSegment(c.Request.Path), b => b.UseStaticFiles(files));
        branch.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET, HEAD";
                return;
            }

            // The SPA fallback: serve index.html through the same middleware, so it gets the same headers and
            // validators. The original path is put back for anything that reads it once the answer is written.
            var original = context.Request.Path;
            context.Request.Path = "/" + IndexFile;
            try
            {
                await next(context);
            }
            finally
            {
                context.Request.Path = original;
            }
        });
        branch.UseStaticFiles(files);

        // Only reached if index.html vanished between the check above and the read.
        branch.Run(NotInstalledAsync);
    }

    private static void ApplyHeaders(StaticFileResponseContext file)
    {
        var request = file.Context.Request;
        var headers = file.Context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        if (file.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = HtmlCacheControl;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
        }
        else if (request.Path.StartsWithSegments(AssetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = AssetCacheControl;
        }
    }

    private static bool HasDotSegment(PathString path) =>
        path.HasValue && path.Value!.Split('/').Any(s => s.StartsWith('.'));

    private static Task NotInstalledAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(NotInstalledText);
    }
}
