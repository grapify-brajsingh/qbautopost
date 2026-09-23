using System.Net;
using System.Net.Http.Json;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-915: the silent no-op. <c>allowModelAccounts</c> shipped through M9 carried on the request record, described in
/// <c>openapi.json</c> as "let the model choose an account for a row no rule resolves", and given its own
/// <c>qb:post:ai</c> scope — while no production line ever read it. A caller could set it, be granted the scope, and
/// receive a batch in which every unresolved row was held as <c>unknown-account</c>, with nothing anywhere saying the
/// model had never been consulted.
/// <para>
/// The fix refuses rather than pretends, and it lives in <c>DirectRequestReader</c> so that <b>validate and post
/// cannot disagree</b> — a validate that accepted what the post refused would be worse than either alone.
/// </para>
/// </summary>
public sealed class ModelAccountsTests : IDisposable
{
    private const string PostRoute = "/api/v1/quickbooks/transactions";
    private const string ValidateRoute = "/api/v1/quickbooks/transactions/validate";

    private readonly ApiFactory _factory = new();

    [Theory]
    [InlineData(PostRoute)]
    [InlineData(ValidateRoute)]
    public async Task Should_Refuse_When_TheCallerAsksForModelChosenAccounts(string route)
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(route, Batch(allowModelAccounts: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.ReadProblemAsync()).ToString();
        Assert.Contains("allowModelAccounts", problem, StringComparison.Ordinal);
        Assert.Contains("Q-51", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PostRoute)]
    [InlineData(ValidateRoute)]
    public async Task Should_NotRefuse_When_TheCallerLeavesModelAccountsAlone(string route)
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(route, Batch(allowModelAccounts: false));

        // Whatever else becomes of this batch, it is not refused for the flag.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseBeforeReachingQuickBooks_When_ModelAccountsAreAskedFor()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(PostRoute, Batch(allowModelAccounts: true));

        // The whole point of refusing in the reader: nothing is sent, and nothing is left half-posted.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Empty(_factory.Hermes.Calls);
    }

    private static object Batch(bool allowModelAccounts) => new
    {
        reference = "t915",
        controlTotal = 10.00m,
        allowModelAccounts,
        transactions = new[]
        {
            new
            {
                kind = "Check",
                date = "2026-08-15",
                amount = 10.00m,
                account = "Operating",
                lineAccount = "Repairs",
                last4 = "1234",
            },
        },
    };

    public void Dispose() => _factory.Dispose();
}
