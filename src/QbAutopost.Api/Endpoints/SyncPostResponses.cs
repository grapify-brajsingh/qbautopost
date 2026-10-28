using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Api;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// T-1014: the status code and body of each validate-and-post ending (validate-and-post §6), in one place, so that
/// <c>POST /quickbooks/transactions/validate-and-post</c> and <c>POST /saved-requests/{id}/post</c> cannot answer the
/// same ending differently (saved-requests spec 7.2, SR-4). The saved route passes <c>extra</c> fields; they are added
/// to the problem's extensions or to the end of the result body, and nothing that is already there is changed.
/// </summary>
internal static class SyncPostResponses
{
    /// <summary>How long §6.4 asks a refused caller to wait.</summary>
    public const int BusyRetryAfterSeconds = 5;

    public static IResult ToResult(SyncPost post, HttpContext http, IDictionary<string, object?>? extra)
    {
        if (post.Outcome == SyncPostOutcome.Busy)
        {
            // §6.4: the header as well as the field, so a client that retries automatically reads the same number a
            // person would.
            http.Response.Headers.RetryAfter = BusyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (ProblemOf(post, extra) is { } problem)
        {
            return TypedResults.Problem(problem);
        }

        if (extra is null)
        {
            // §6.5: the backup guard, or QuickBooks unreachable. Nothing was sent, so a retry is safe.
            return post.Unavailable
                ? Results.Json(post.Result, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(post.Result);
        }

        var options = OptionsOf(http);
        return Results.Json(BodyOf(post.Result!, extra, options), options, statusCode: StatusOf(post));
    }

    /// <summary>The status code <see cref="ToResult"/> answers for <paramref name="post"/>.</summary>
    public static int StatusOf(SyncPost post) => post.Outcome switch
    {
        SyncPostOutcome.Refused => StatusCodes.Status400BadRequest,
        SyncPostOutcome.Held => StatusCodes.Status422UnprocessableEntity,
        SyncPostOutcome.Busy => StatusCodes.Status409Conflict,
        _ => post.Unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK,
    };

    /// <summary>
    /// The body <see cref="ToResult"/> writes, as JSON, for the saved-request attempt record. The problem-details
    /// writer adds the exchange's own <c>requestId</c> and <c>traceId</c> on the way out; those name the HTTP request,
    /// not the result, and are not part of this.
    /// </summary>
    public static JsonNode BodyOf(SyncPost post, IDictionary<string, object?>? extra, JsonSerializerOptions options) =>
        ProblemOf(post, extra) is { } problem
            ? JsonSerializer.SerializeToNode(problem, options)!
            : BodyOf(post.Result!, extra, options);

    /// <summary>The serialiser options every minimal-API answer of this host is written with.</summary>
    public static JsonSerializerOptions OptionsOf(IServiceProvider services) =>
        services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

    private static JsonSerializerOptions OptionsOf(HttpContext http) => OptionsOf(http.RequestServices);

    private static JsonObject BodyOf(SyncPostResult result, IDictionary<string, object?>? extra, JsonSerializerOptions options)
    {
        var body = JsonSerializer.SerializeToNode(result, options)!.AsObject();
        foreach (var (key, value) in extra ?? new Dictionary<string, object?>())
        {
            body[key] = JsonSerializer.SerializeToNode(value, options);
        }

        return body;
    }

    /// <summary>The problem for a 400, 422 or 409 ending (with the defaults <c>Results.Problem</c> applies); null for the rest.</summary>
    private static ProblemDetails? ProblemOf(SyncPost post, IDictionary<string, object?>? extra)
    {
        ProblemHttpResult? result = post.Outcome switch
        {
            SyncPostOutcome.Refused => TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The request could not be read",
                detail: string.Join("; ", post.Errors),
                extensions: With(extra, new Dictionary<string, object?> { ["errors"] = post.Errors })),

            // §6.2: every finding, because §5.1 refuses the batch on one of them — a caller fixing them one round trip
            // at a time is the failure this format exists to avoid.
            SyncPostOutcome.Held => TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The transactions were not posted",
                detail: post.Findings!.Detail,
                extensions: With(extra, new Dictionary<string, object?>
                {
                    ["counts"] = post.Findings.Counts,
                    ["validation"] = post.Findings.Validation,
                    ["unknownNames"] = post.Findings.UnknownNames,
                })),

            SyncPostOutcome.Busy => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "QuickBooks is busy with another operation",
                detail: "A folder job or another post is using the QuickBooks session. Nothing was sent. "
                        + $"Retry in {BusyRetryAfterSeconds} seconds.",
                extensions: With(extra, new Dictionary<string, object?> { ["retryAfterSeconds"] = BusyRetryAfterSeconds })),

            _ => null,
        };

        return result?.ProblemDetails;
    }

    private static Dictionary<string, object?> With(IDictionary<string, object?>? extra, Dictionary<string, object?> extensions)
    {
        foreach (var (key, value) in extra ?? new Dictionary<string, object?>())
        {
            extensions[key] = value;
        }

        return extensions;
    }
}
