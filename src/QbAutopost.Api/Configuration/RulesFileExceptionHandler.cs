using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using QbAutopost.Core.Mapping;

namespace QbAutopost.Api.Configuration;

/// <summary>
/// T-920: turns <see cref="RulesFileNotFoundException"/> into the RFC 7807 answer the conventions promise.
/// <para>
/// Before this, a request that needed the rules file when it was not there threw, and the caller got a bare
/// <c>500</c> with no body worth reading while the operator got a stack trace. The condition is a
/// <b>misconfiguration</b>, not a fault: the server is running but cannot do this work, which is what <c>503</c>
/// says — and the same thing <c>/health/ready</c> has been saying all along.
/// </para>
/// <para>
/// The detail names the resolved path deliberately. The defect this came from was a relative path resolving against
/// the content root instead of the project folder, and every message about it said only that something was missing,
/// never <i>where the server looked</i> — which is the one fact that would have ended it in a minute.
/// </para>
/// </summary>
public sealed class RulesFileExceptionHandler(
    IProblemDetailsService problems, ILogger<RulesFileExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not RulesFileNotFoundException missing)
        {
            // Not ours: let the default handler answer, so a real fault still looks like one.
            return false;
        }

        log.LogError(
            "Request {Method} {Path} could not be served: the rules file {RulesFile} does not exist. "
            + "Company:RulesFile is resolved against the content root (the executable's own folder), not the "
            + "working directory - a relative path is relative to that.",
            context.Request.Method, context.Request.Path.Value, missing.FileName);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = Title,
                Detail = DetailOf(missing),
            },
        });
    }

    public const string Title = "The rules file is missing";

    /// <summary>T-1014: shared with the post-by-id route, which records this same answer on the attempt.</summary>
    public static string DetailOf(RulesFileNotFoundException missing) =>
        $"Company:RulesFile resolved to '{missing.FileName}' and nothing is there. Relative paths are "
        + "resolved against the content root - the folder the executable is in - not the working "
        + "directory. Check /health/ready, which reports this before a request has to.";
}
