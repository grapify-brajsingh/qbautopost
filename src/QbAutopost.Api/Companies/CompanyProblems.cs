namespace QbAutopost.Api.Companies;

/// <summary>
/// RFC 7807 answers of the company routes; <c>type</c> is the code (M17 global constraints). T-1708 adds Required,
/// NotReady, OpenElsewhere and FileUnavailable; T-1710 adds QuickBooksNotOpen.
/// </summary>
public static class CompanyProblems
{
    public const string RootUnavailableType = "company-root-unavailable";

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
}
