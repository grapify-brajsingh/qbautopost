using System.Text.Json;
using QbAutopost.Core.Mapping;

namespace QbAutopost.Api.Endpoints;

/// <summary>Rules teaching routes (spec §6, FR-14): <c>POST /rules/alias</c> and <c>POST /rules/account</c>.</summary>
public static class RulesEndpoints
{
    public sealed record AliasRequest(string? Fragment, string? Name, string? Kind);

    public sealed record AccountRuleRequest(string? Vendor, string? Account);

    public static IEndpointRouteBuilder MapRulesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/rules/alias", SetAlias);
        app.MapPost("/rules/account", SetAccount);
        return app;
    }

    private static IResult SetAlias(AliasRequest request, RulesEditor editor, ILogger<RulesEditor> log)
    {
        if (!TryParseKind(request.Kind, out var kind))
        {
            return Invalid("kind must be \"vendor\" or \"customer\".");
        }

        return Apply(() => editor.SetAlias(request.Fragment, request.Name, kind), log);
    }

    private static IResult SetAccount(AccountRuleRequest request, RulesEditor editor, ILogger<RulesEditor> log) =>
        Apply(() => editor.SetVendorAccount(request.Vendor, request.Account), log);

    private static IResult Apply(Func<RuleChange> change, ILogger log)
    {
        try
        {
            var result = change();
            log.LogInformation(
                "Rule taught: {Section} [{Key}] = {Value} (was {Previous})", result.Section, result.Key, result.Value, result.Previous);
            return Results.Ok(result);
        }
        catch (RuleValidationException ex)
        {
            return Invalid(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // SPEC-GAP T-701: an unreadable rules.json is never repaired or recreated by teaching.
            log.LogError(ex, "Rule not taught: rules.json cannot be read or written");
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "rules.json cannot be updated", detail: ex.Message);
        }
    }

    private static bool TryParseKind(string? value, out AliasKind kind)
    {
        kind = AliasKind.Vendor;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "vendor":
                return true;
            case "customer":
                kind = AliasKind.Customer;
                return true;
            default:
                return false;
        }
    }

    private static IResult Invalid(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Rule not accepted", detail: detail);
}
