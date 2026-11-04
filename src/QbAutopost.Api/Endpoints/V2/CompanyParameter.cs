using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>
/// M17 §5.3, review focus 4: the <c>company=&lt;id&gt;</c> of a v2 route (for sync also <c>{"company": n}</c> in the
/// body). Absent is the default company. A value that is not a positive integer, a value given more than once (twice in
/// the query, or in both query and body) and an id no company has are input errors (400 <c>invalid-request</c>); none
/// ever falls back to the default company.
/// </summary>
public static class CompanyParameter
{
    public const string Name = "company";

    /// <summary>
    /// Absent → true with a null company (the default company). Not a positive integer, repeated, or unknown → false with
    /// a 400 detail. <paramref name="body"/> is the request's JSON body, when the route takes one.
    /// </summary>
    public static async Task<(bool Ok, CompanyContext? Company, string? Error)> ReadAsync(
        HttpContext http, ICompanyResolver resolver, JsonElement? body, CancellationToken ct)
    {
        if (!TryParse(http, body, out var id, out var error))
        {
            return (false, null, error);
        }

        if (id is null)
        {
            return (true, null, null);
        }

        return await resolver.GetAsync(id.Value, ct) is { } company
            ? (true, company, null)
            : (false, null, $"There is no company {id}.");
    }

    /// <summary>The syntax half of <see cref="ReadAsync"/>: the id given, null when absent; an unknown id is not checked.</summary>
    public static bool TryParse(HttpContext http, JsonElement? body, out int? id, out string? error)
    {
        id = null;
        error = null;
        var inQuery = http.Request.Query.TryGetValue(Name, out var values);
        var inBody = body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(Name, out _);
        if (body is { } json && json.ValueKind != JsonValueKind.Object)
        {
            error = "The body must be a JSON object.";
            return false;
        }

        if ((inQuery && values.Count != 1) || (inQuery && inBody))
        {
            error = "company may be given once.";
            return false;
        }

        if (inQuery)
        {
            if (int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                id = parsed;
                return true;
            }
        }
        else if (inBody)
        {
            var value = body!.Value.GetProperty(Name);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed > 0)
            {
                id = parsed;
                return true;
            }
        }
        else
        {
            return true;
        }

        error = "company must be a company id (a positive integer).";
        return false;
    }

    /// <summary>
    /// The JSON body of a route that may carry <c>company</c>: null when there is none (the server says the request can
    /// have no body, or it has zero bytes or only white space). False when it is not JSON (a 400 detail). The body is
    /// read to its end, never judged by headers alone: an HTTP/2 or HTTP/3 body may come without a length or
    /// <c>Transfer-Encoding</c>, and dropping it would sync the default company instead of the one asked for. Its size is
    /// bounded by the request size limits.
    /// </summary>
    public static async Task<(bool Ok, JsonElement? Body, string? Error)> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return (true, null, null);
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.All(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        {
            return (true, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return (true, document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (false, null, "The body is not valid JSON.");
        }
    }
}
