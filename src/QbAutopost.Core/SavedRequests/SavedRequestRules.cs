namespace QbAutopost.Core.SavedRequests;

/// <summary>Scope names for saved requests. T-1009 moves or aliases these into the shared scope list.</summary>
public static class SavedRequestScopes
{
    public const string Submit = "requests:submit";
    public const string Read = "requests:read";
    public const string Edit = "requests:edit";
    public const string Post = "requests:post";
    public const string Admin = "requests:admin";
}

/// <summary>Which actions a status and a set of scopes allow (spec §7.1). Pure; no I/O.</summary>
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
                if (scopes.Contains(SavedRequestScopes.Edit))
                {
                    allowed |= SavedRequestAction.Edit | SavedRequestAction.Validate | SavedRequestAction.Discard;
                }

                if (scopes.Contains(SavedRequestScopes.Post))
                {
                    allowed |= SavedRequestAction.Post;
                }

                break;
            case SavedRequestStatus.Discarded:
                if (scopes.Contains(SavedRequestScopes.Admin))
                {
                    allowed |= SavedRequestAction.Restore;
                }

                break;
            case SavedRequestStatus.Partial:
            case SavedRequestStatus.Unknown:
                if (scopes.Contains(SavedRequestScopes.Admin))
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

    /// <summary>Unknown may resolve to posted or not-posted; partial only to posted (some rows already landed).</summary>
    public static bool CanResolveTo(SavedRequestStatus from, ResolveOutcome to) => from switch
    {
        SavedRequestStatus.Unknown => true,
        SavedRequestStatus.Partial => to == ResolveOutcome.Posted,
        _ => false,
    };
}
