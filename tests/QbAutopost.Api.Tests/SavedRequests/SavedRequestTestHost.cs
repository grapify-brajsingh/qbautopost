using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1012: the usual in-memory host (fake store, fake gate, fake users) plus an n8n key holding only
/// <c>requests:submit</c>, as the spec's n8n workflow would be issued.
/// </summary>
public sealed class SavedRequestTestHost : IDisposable
{
    public const string Route = "/api/v1/saved-requests";
    public const string N8nClientId = "n8n-mailbox";
    public const string N8nClientName = "n8n mailbox workflow";

    public ApiFactory Factory { get; } = new();

    public string N8nKey { get; }

    public SavedRequestTestHost()
    {
        N8nKey = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(N8nKey);
        var file = Factory.Dir.Combine("data", "clients.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        new ApiClientStore(file).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = N8nClientId,
                    Name = N8nClientName,
                    KeyHash = secret.KeyHash,
                    KeySalt = secret.KeySalt,
                    Scopes = [ApiScopes.RequestsSubmit],
                    CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });
    }

    public static string Fixture(string name) => File.ReadAllText(Fixtures.PathOf("saved-requests", name), Encoding.UTF8);

    public static byte[] FixtureBytes(string name) => File.ReadAllBytes(Fixtures.PathOf("saved-requests", name));

    public HttpClient N8n()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", N8nKey);
        return client;
    }

    /// <summary>A signed-in dashboard user of <paramref name="role"/>; their id comes back in <paramref name="userId"/>.</summary>
    public HttpClient User(string role, out string userId)
    {
        var token = Factory.Users.TokenFor(role);
        userId = Factory.Users.IdOf(token);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient User(string role) => User(role, out _);

    public static HttpContent Json(string body) => new StringContent(body, Encoding.UTF8, "application/json");

    public static HttpContent Json(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Saves <paramref name="body"/> as n8n and returns the answer's status and body.</summary>
    public async Task<(HttpResponseMessage Response, JsonElement Body)> SaveAsync(string body, string? idempotencyKey = null, HttpClient? client = null)
    {
        var own = client is null;
        client ??= N8n();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Json(body) };
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
        finally
        {
            if (own)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Saves a fixture and returns the new id.</summary>
    public async Task<long> SaveFixtureAsync(string name)
    {
        var (response, body) = await SaveAsync(Fixture(name));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        response.Dispose();
        return body.GetProperty("id").GetInt64();
    }

    public static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public void Dispose() => Factory.Dispose();
}
