using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Tests.Configuration;

/// <summary>
/// T-901 (api-v1 §12), session-13 server defect 1: started from another folder the exe read no
/// <c>appsettings.json</c> at all and ran with an empty <c>Company:Name</c> and Hermes enabled. The content root must
/// therefore be the executable's own folder — except under the test host, which sets its own.
/// </summary>
public sealed class ContentRootTests
{
    private const string BaseDirectory = @"C:\app\";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Should_UseBaseDirectory_When_EnvironmentIsNotTesting(string? environment)
    {
        Assert.Equal(BaseDirectory, ContentRoot.Select(environment, BaseDirectory));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("testing")]
    public void Should_KeepHostDefault_When_EnvironmentIsTesting(string environment)
    {
        Assert.Null(ContentRoot.Select(environment, BaseDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_KeepHostDefault_When_BaseDirectoryIsBlank(string? baseDirectory)
    {
        Assert.Null(ContentRoot.Select("Production", baseDirectory));
    }
}
