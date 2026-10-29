using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using Serilog.Events;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// M12 final review: the strict-schema switch (Ruling R16), the 504 detail naming the timeout that fired, multipart
/// errors that are not a size problem, and Kestrel's limit when the upload is off.
/// </summary>
public sealed class ExtractionFinalReviewTests : IDisposable
{
    private const string Boundary = "final-review-boundary";

    private readonly List<ExtractionTestHost> _hosts = [];

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private ExtractionTestHost Host(params (string Key, string? Value)[] pairs)
    {
        var host = new ExtractionTestHost(pairs.ToDictionary(p => p.Key, p => p.Value));
        _hosts.Add(host);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(ExtractionTestHost host, HttpContent content)
    {
        using var client = host.User(DashboardRoles.Editor);
        var (response, body) = await host.PostAsync(client, content);
        return (response.StatusCode, body);
    }

    private static ByteArrayContent Raw(string body)
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={Boundary}");
        return content;
    }

    private static void AssertNoErrorLogged(ExtractionTestHost host) =>
        Assert.DoesNotContain(host.Logs.Events, e => e.Level >= LogEventLevel.Error);

    // ---- Extraction:StrictSchema (R16) ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_SendStrictTrue_When_StrictSchemaDefault()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");

        var (status, _) = await UploadAsync(host, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Assert.Single(host.Handler.Requests).Json["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Should_SendStrictFalse_When_StrictSchemaTurnedOff()
    {
        var host = Host(("Extraction:StrictSchema", "false"));
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");

        var (status, _) = await UploadAsync(host, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(Assert.Single(host.Handler.Requests).Json["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public void Should_DefaultStrictSchemaToTrue_When_NotConfigured() =>
        Assert.True(new ExtractionSettings().StrictSchema);

    // ---- the 504 detail -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_NameExtractionTimeout_When_ExtractionTimeoutFires()
    {
        var host = Host(("Extraction:TimeoutSeconds", "1"), ("OpenRouter:TimeoutSeconds", "300"));
        host.Handler.ReplyNever();

        var (status, body) = await UploadAsync(host, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        var detail = body.GetProperty("detail").GetString()!;
        Assert.Contains("within 1 s", detail, StringComparison.Ordinal);
        Assert.Contains("Extraction:TimeoutSeconds", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRouter:TimeoutSeconds", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NameOpenRouterTimeout_When_ClientTimeoutFiresFirst()
    {
        var host = Host(("Extraction:TimeoutSeconds", "300"), ("OpenRouter:TimeoutSeconds", "1"));
        host.Handler.ReplyNever();

        var (status, body) = await UploadAsync(host, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Equal("extraction-timeout", body.GetProperty("type").GetString());
        var detail = body.GetProperty("detail").GetString()!;
        Assert.Contains("within 1 s", detail, StringComparison.Ordinal);
        Assert.Contains("OpenRouter:TimeoutSeconds", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("300", detail, StringComparison.Ordinal);
    }

    // ---- multipart errors that are not a size problem ----------------------------------------------------------------

    [Fact]
    public async Task Should_Return400_When_MultipartHeaderLineMalformed()
    {
        var host = Host();
        var body = $"--{Boundary}\r\nthis header line has no colon\r\n\r\n%PDF-1.4\r\n--{Boundary}--\r\n";

        var (status, problem) = await UploadAsync(host, Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", problem.GetProperty("type").GetString());
        Assert.Empty(host.Handler.Requests);
        AssertNoErrorLogged(host);
    }

    [Fact]
    public async Task Should_Return400NotFileTooLarge_When_MultipartHeaderTooLong()
    {
        var host = Host();
        var body = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.pdf\"\r\n"
            + "X-Pad: " + new string('a', 20000) + "\r\n\r\n%PDF-1.4\r\n--" + Boundary + "--\r\n";

        var (status, problem) = await UploadAsync(host, Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", problem.GetProperty("type").GetString());
        Assert.Empty(host.Handler.Requests);
        AssertNoErrorLogged(host);
    }

    [Fact]
    public async Task Should_Return400_When_MultipartBodyTruncated()
    {
        var host = Host();
        var body = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.pdf\"\r\n\r\n%PDF-1.4 and then the stream ends";

        var (status, problem) = await UploadAsync(host, Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", problem.GetProperty("type").GetString());
        Assert.Empty(host.Handler.Requests);
        AssertNoErrorLogged(host);
    }

    [Fact]
    public async Task Should_Return413_When_MultipartBodyOverBodyLimit()
    {
        // A small file part, then an ignored part that takes the body past MaxBytes + 64 KiB.
        var host = Host(("Extraction:MaxBytes", "1024"));
        var body = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"other\"\r\n\r\n" + new string('x', 70 * 1024)
            + $"\r\n--{Boundary}--\r\n";
        var content = Raw(body);
        content.Headers.ContentLength = null;

        var (status, problem) = await UploadAsync(host, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("file-too-large", problem.GetProperty("type").GetString());
    }

    // ---- Kestrel's limit -----------------------------------------------------------------------------------------------

    [Fact]
    public void Should_KeepKestrelAtApiLimit_When_ExtractionDisabled()
    {
        var api = new ApiSettings();

        Assert.Equal(api.MaxRequestBodyBytes, RequestSizeMiddleware.KestrelLimit(api, new ExtractionSettings { Enabled = false }));
    }

    [Fact]
    public void Should_RaiseKestrelLimit_When_ExtractionEnabled()
    {
        var api = new ApiSettings();

        Assert.Equal(10485760 + 64 * 1024, RequestSizeMiddleware.KestrelLimit(api, new ExtractionSettings { Enabled = true }));
    }
}
