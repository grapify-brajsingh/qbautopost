using QbAutopost.Core.Api;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Companies;

/// <summary>
/// RFC 7807 answers of the company routes; <c>type</c> is the code (M17 global constraints). T-1708 adds Required,
/// NotReady, OpenElsewhere and FileUnavailable; T-1710 adds QuickBooksNotOpen.
/// </summary>
public static class CompanyProblems
{
    public const string RootUnavailableType = "company-root-unavailable";
    public const string RequiredType = "company-required";
    public const string NotReadyType = "company-not-ready";

    /// <summary>503. <paramref name="rootFolder"/> is the folder that cannot be read (or the reason when none is set); nothing was changed.</summary>
    public static IResult RootUnavailable(string rootFolder) =>
        Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Company root unavailable",
            detail: $"The company root folder is unavailable: {rootFolder}. Nothing was changed.",
            type: RootUnavailableType);

    public static IResult InvalidRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail, type: "invalid-request");

    public static IResult NotFound(int id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Company not found", detail: $"There is no company {id}.", type: "not-found");

    public static IResult PreconditionRequired() =>
        Results.Problem(
            statusCode: StatusCodes.Status428PreconditionRequired,
            title: "If-Match required",
            detail: "Send the company's ETag in If-Match (GET /companies returns rowVersion; a PATCH answer returns the new ETag).",
            type: "precondition-required");

    public static IResult EditedBySomeoneElse(int rowVersion) =>
        Results.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            title: "Edited by someone else",
            detail: "The company changed since you loaded it. Reload it and try again. Nothing was changed.",
            type: "edited-by-someone-else",
            extensions: new Dictionary<string, object?> { ["rowVersion"] = rowVersion });

    /// <summary>409 (spec 5.2): the statement has no company, so nothing is validated or posted. Used from T-1712.</summary>
    public static IResult Required(string publicRef) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Company required",
            detail: $"{publicRef} has no company yet. Set its company, then validate or post it. Nothing was sent.",
            type: RequiredType);

    /// <summary>
    /// 409 (spec 4.7): only an <c>active</c> + <c>approved</c> company opens a QuickBooks session. Nothing was claimed and
    /// no session was opened.
    /// </summary>
    public static IResult NotReady(CompanyContext company) =>
        NotReadyProblem(
            company.FileOutsideRoot
                ? $"{company.DisplayName}'s company file is outside Companies:RootFolder, so it is never opened. Nothing was sent."
                : $"{company.DisplayName} is {CompanyText.Of(company.State)} with access {CompanyText.Of(company.Access)}; only an "
                  + "active, approved company can be validated or posted. Nothing was sent.",
            CompanyText.Of(company.State),
            CompanyText.Of(company.Access));

    /// <summary>Ruling B18: a stored statement names a company that no longer exists. 409, nothing claimed, no session.</summary>
    public static IResult NotReady(int companyId) =>
        NotReadyProblem($"Company {companyId} of this statement no longer exists. Set its company again. Nothing was sent.", null, null);

    /// <summary>409 (spec 4.7, A-7): QuickBooks has another company file open. Nothing was sent and it is not retried.</summary>
    public static IResult OpenElsewhere(CompanyFileRefusal refusal, IDictionary<string, object?>? extra = null) =>
        Results.Problem(OpenElsewhereProblem(refusal, extra));

    /// <summary>503 (spec 4.7, ruling B1): the company file's session could not open. Nothing was sent and it is not retried.</summary>
    public static IResult FileUnavailable(CompanyFileRefusal refusal, IDictionary<string, object?>? extra = null) =>
        Results.Problem(FileUnavailableProblem(refusal, extra));

    /// <summary>The problem a company-file refusal answers (409 or 503), with the saved route's extra fields.</summary>
    public static Microsoft.AspNetCore.Mvc.ProblemDetails ProblemOf(CompanyFileRefusal refusal, IDictionary<string, object?>? extra) =>
        refusal.Type == CompanyFileRefusal.OpenElsewhere ? OpenElsewhereProblem(refusal, extra) : FileUnavailableProblem(refusal, extra);

    /// <summary>The status code of <see cref="ProblemOf"/>.</summary>
    public static int StatusOf(CompanyFileRefusal refusal) =>
        refusal.Type == CompanyFileRefusal.OpenElsewhere ? StatusCodes.Status409Conflict : StatusCodes.Status503ServiceUnavailable;

    private static Microsoft.AspNetCore.Mvc.ProblemDetails OpenElsewhereProblem(CompanyFileRefusal refusal, IDictionary<string, object?>? extra) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Another company file is open",
            detail: (refusal.OpenFile is { } open ? $"QuickBooks has {open} open" : "QuickBooks has another company file open")
                    + $", so {refusal.NeededFile} could not be opened. Nothing was sent. Close that file in QuickBooks (this app "
                    + "never switches it), then post again.",
            type: CompanyFileRefusal.OpenElsewhere,
            extensions: With(extra, new Dictionary<string, object?> { ["openFile"] = refusal.OpenFile, ["neededFile"] = refusal.NeededFile }))
            .ProblemDetails;

    private static Microsoft.AspNetCore.Mvc.ProblemDetails FileUnavailableProblem(CompanyFileRefusal refusal, IDictionary<string, object?>? extra) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Company file unavailable",
            detail: $"QuickBooks could not open {refusal.NeededFile} ({refusal.SdkMessage}). Nothing was sent and it was not "
                    + "retried. Check that QuickBooks can open the file, then post again.",
            type: CompanyFileRefusal.Unavailable,
            extensions: With(extra, new Dictionary<string, object?> { ["neededFile"] = refusal.NeededFile }))
            .ProblemDetails;

    private static IResult NotReadyProblem(string detail, string? state, string? access) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Company not ready",
            detail: detail,
            type: NotReadyType,
            extensions: new Dictionary<string, object?> { ["state"] = state, ["access"] = access });

    private static Dictionary<string, object?> With(IDictionary<string, object?>? extra, Dictionary<string, object?> extensions)
    {
        foreach (var (key, value) in extra ?? new Dictionary<string, object?>())
        {
            extensions[key] = value;
        }

        return extensions;
    }
}
