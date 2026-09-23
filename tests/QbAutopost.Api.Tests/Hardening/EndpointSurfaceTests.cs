using System.Reflection;
using QbAutopost.Api.Endpoints;

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
