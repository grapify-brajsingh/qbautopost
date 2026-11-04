using QbAutopost.Core.Security;

namespace QbAutopost.Core.SavedRequests;

/// <summary>Which actions a status and a set of scopes allow (spec §7.1). Pure; no I/O.</summary>
/// <summary>
/// M17 spec 4.6, 4.7: what a statement's company allows. <see cref="Required"/>: it has none; <see cref="NotReady"/>: its
/// company may not open a QuickBooks session (not active + approved, file outside the root, or gone).
/// </summary>
public enum CompanyGate { Ready, Required, NotReady }

public static class SavedRequestRules
{
    private static readonly (SavedRequestAction Action, string Name)[] Names =
    [
        (SavedRequestAction.Edit, "edit"),
        (SavedRequestAction.Validate, "validate"),
        (SavedRequestAction.Post, "post"),
        (SavedRequestAction.Discard, "discard"),
        (SavedRequestAction.Restore, "restore"),
        (SavedRequestAction.Resolve, "resolve"),
        (SavedRequestAction.SetCompany, "set-company"),
    ];

    /// <summary>API clients (<paramref name="isUser"/> false) never get an action: only dashboard users act on saved requests.</summary>
    public static SavedRequestAction Allowed(SavedRequestStatus status, IReadOnlySet<string> scopes, bool isUser)
    {
        if (!isUser)
        {
            return SavedRequestAction.None;
        }

        var allowed = SavedRequestAction.None;
        switch (status)
        {
            case SavedRequestStatus.Pending:
                if (scopes.Contains(ApiScopes.RequestsEdit))
                {
                    allowed |= SavedRequestAction.Edit | SavedRequestAction.Validate | SavedRequestAction.Discard;
                }

                if (scopes.Contains(ApiScopes.RequestsPost))
                {
                    allowed |= SavedRequestAction.Post;
                }

                break;
            case SavedRequestStatus.Discarded:
                if (scopes.Contains(ApiScopes.RequestsAdmin))
                {
                    allowed |= SavedRequestAction.Restore;
                }

                break;
            case SavedRequestStatus.Partial:
            case SavedRequestStatus.Unknown:
                if (scopes.Contains(ApiScopes.RequestsAdmin))
                {
                    allowed |= SavedRequestAction.Resolve;
                }

                break;
        }

        return allowed;
    }

    /// <summary>Lower-case action names in the order edit, validate, post, discard, restore, resolve.</summary>
    public static IReadOnlyList<string> AllowedActionNames(
        SavedRequestStatus status, IReadOnlySet<string> scopes, bool isUser)
    {
        var allowed = Allowed(status, scopes, isUser);
        return Names.Where(n => allowed.HasFlag(n.Action)).Select(n => n.Name).ToArray();
    }

    /// <summary>
    /// v2 only (M17 spec 4.6, 4.7). The v1 actions, then the company gate: a statement without a ready company has no
    /// <c>validate</c> or <c>post</c>; a pending one offers <c>set-company</c> to a <c>requests:edit</c> user whatever its
    /// company (ruling B16: this is the one place the rule is written).
    /// </summary>
    public static SavedRequestAction Allowed(
        SavedRequestStatus status, IReadOnlySet<string> scopes, bool isUser, CompanyGate company)
    {
        var allowed = Allowed(status, scopes, isUser);
        if (company != CompanyGate.Ready)
        {
            allowed &= ~(SavedRequestAction.Validate | SavedRequestAction.Post);
        }

        if (isUser && status == SavedRequestStatus.Pending && scopes.Contains(ApiScopes.RequestsEdit))
        {
            allowed |= SavedRequestAction.SetCompany;
        }

        return allowed;
    }

    /// <summary>v2 only: <see cref="AllowedActionNames(SavedRequestStatus, IReadOnlySet{string}, bool)"/>'s order, then <c>set-company</c>.</summary>
    public static IReadOnlyList<string> AllowedActionNames(
        SavedRequestStatus status, IReadOnlySet<string> scopes, bool isUser, CompanyGate company)
    {
        var allowed = Allowed(status, scopes, isUser, company);
        return Names.Where(n => allowed.HasFlag(n.Action)).Select(n => n.Name).ToArray();
    }

    /// <summary>Unknown may resolve to posted or not-posted; partial only to posted (some rows already landed).</summary>
    public static bool CanResolveTo(SavedRequestStatus from, ResolveOutcome to) => from switch
    {
        SavedRequestStatus.Unknown => true,
        SavedRequestStatus.Partial => to == ResolveOutcome.Posted,
        _ => false,
    };
}
