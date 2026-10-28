namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>Every action of the audit feed (spec saved-requests-dashboard-v2 8.3; the additions are Q-97).</summary>
public static class AuditActions
{
    public const string Received = "received";
    public const string FlaggedDuplicate = "flagged-duplicate";
    public const string Validated = "validated";
    public const string PostStarted = "post-started";
    public const string Posted = "posted";
    public const string PostFailed = "post-failed";
    public const string Discarded = "discarded";
    public const string Restored = "restored";
    public const string Resolved = "resolved";

    public const string RowAdded = "row-added";
    public const string RowDeleted = "row-deleted";
    public const string RowPostOff = "row-post-off";
    public const string RowPostOn = "row-post-on";
    public const string RowEdited = "row-edited";
    public const string ControlTotalChanged = "control-total-changed";
    public const string RequestEdited = "request-edited";

    public const string UserCreated = "user-created";
    public const string UserRoleChanged = "user-role-changed";
    public const string UserDisabled = "user-disabled";
    public const string UserEnabled = "user-enabled";
    public const string UserPasswordReset = "user-password-reset";
    public const string UserUnlocked = "user-unlocked";
    public const string UserPasswordChanged = "user-password-changed";
    public const string UserSignedIn = "user-signed-in";
    public const string UserSignInFailed = "user-sign-in-failed";
    public const string UserLockedOut = "user-locked-out";
    public const string UserSessionRevoked = "user-session-revoked";
    public const string UserOther = "user-other";

    public static IReadOnlyList<string> All { get; } =
    [
        Received, FlaggedDuplicate, Validated, PostStarted, Posted, PostFailed, Discarded, Restored, Resolved,
        RowAdded, RowDeleted, RowPostOff, RowPostOn, RowEdited, ControlTotalChanged, RequestEdited,
        UserCreated, UserRoleChanged, UserDisabled, UserEnabled, UserPasswordReset, UserUnlocked, UserPasswordChanged,
        UserSignedIn, UserSignInFailed, UserLockedOut, UserSessionRevoked, UserOther,
    ];
}

/// <summary><c>actor.kind</c> of the audit feed (spec saved-requests-dashboard-v2 8.2; <c>system</c> and <c>anonymous</c> are Q-97).</summary>
public static class AuditActorKinds
{
    public const string User = "user";
    public const string ApiClient = "api-client";
    public const string System = "system";
    public const string Anonymous = "anonymous";
}
