using System.Net;
using System.Net.Http.Json;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>T-1102 / O-1: <c>notPosted</c> is engine input, never wire input, so v1 binding ignores it as before.</summary>
public sealed class V1NotPostedBindingTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly object[] Rows =
    [
        new
        {
            externalId = "row-1",
            kind = "Check",
            date = "2026-08-05",
            amount = 184.32m,
            account = "Chase Checking 4521",
            payee = "HOME DEPOT #6412",
            memo = "HOME DEPOT #6412 PURCHASE",
            last4 = "4521",
        },
    ];

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(object request)
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/transactions/validate", request, ApiFactory.Json);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Should_IgnoreNotPostedMember_When_V1ValidateBodyHasIt()
    {
        var without = await PostAsync(new { reference = "r1", controlTotal = 184.32m, transactions = Rows });
        var with = await PostAsync(new { reference = "r1", controlTotal = 184.32m, transactions = Rows, notPosted = new[] { 0 } });

        Assert.Equal(without.Status, with.Status);
        Assert.Equal(without.Body, with.Body);
    }
}
