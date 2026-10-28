using System.Security.Cryptography;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-1101 (dashboard API v2, O-1): the v1 OpenAPI document is the v1 contract. Adding <c>/api/v2</c> must not change
/// a byte of it, so its hash is pinned. Changing <c>wwwroot/openapi.json</c> on purpose means updating the constant
/// below in the same commit, with a tracker note.
/// </summary>
public sealed class V1ContractPinTests
{
    private const string V1OpenApiSha256 = "61b91f299376c511d79df2eab894c0ddbe1995aeaf9fbabdc5afed8028dbaaad";

    [Fact]
    public void Should_KeepV1OpenApiDocumentByteIdentical_When_V2IsAdded()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "QbAutopost.Api", "wwwroot", "openapi.json");

        // Git may check the file out with CRLF (.gitattributes: text=auto); the pin is of the committed LF bytes.
        var bytes = File.ReadAllBytes(path).Where(b => b != (byte)'\r').ToArray();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(V1OpenApiSha256, actual);
    }

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
