using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>
/// The audit mapping table (spec saved-requests-dashboard-v2 8.3) in C#. The PostgreSQL view <c>audit_events</c>
/// (migration AuditEventsView) is the same table in SQL, rule for rule and in the same order; the fake store uses
/// this class. <c>AuditEventsStoreContract.Should_MatchMapperOutput_When_ViewRead</c> holds the two together, so a
/// change here needs the same change in the view, and the other way round.
/// </summary>
public static class AuditEventMapper
{
    private const string ApiClientPrefix = "api-client:";
    private const string RefreshReuse = "refresh-reuse";

    // Same patterns as the view. \z, not $: in .NET $ also matches before a final newline, in PostgreSQL it does not.
    private static readonly Regex RowPath = new(@"^\$\.transactions\[([0-9]+)\](.*)\z", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex RootPrefix = new(@"^\$\.?", RegexOptions.CultureInvariant);
    private static readonly Regex WordBoundary = new("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> UserActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["created"] = AuditActions.UserCreated,
        ["role-changed"] = AuditActions.UserRoleChanged,
        ["disabled"] = AuditActions.UserDisabled,
        ["enabled"] = AuditActions.UserEnabled,
        ["password-reset"] = AuditActions.UserPasswordReset,
        ["unlocked"] = AuditActions.UserUnlocked,
        ["password-changed"] = AuditActions.UserPasswordChanged,
        ["login"] = AuditActions.UserSignedIn,
        ["login-failed"] = AuditActions.UserSignInFailed,
        ["locked-out"] = AuditActions.UserLockedOut,
    };

    /// <summary><c>system</c>, <c>anonymous</c>, <c>api-client</c> for the <c>api-client:</c> prefix, otherwise <c>user</c>.</summary>
    public static string ActorKindOf(string actor) => actor switch
    {
        "system" => AuditActorKinds.System,
        "anonymous" => AuditActorKinds.Anonymous,
        _ when actor.StartsWith(ApiClientPrefix, StringComparison.Ordinal) => AuditActorKinds.ApiClient,
        _ => AuditActorKinds.User,
    };

    /// <summary>A timeline event; null for <c>edited</c>, whose changes are mapped by <see cref="FromVersion"/>.</summary>
    public static AuditEvent? FromEvent(SavedRequestEvent e)
    {
        if (e.Kind == SavedRequestEvents.Edited)
        {
            return null;
        }

        var d = e.Detail;
        var action = e.Kind switch
        {
            SavedRequestEvents.Created => AuditActions.Received,
            SavedRequestEvents.PostFinished => Kebab(Text(d, "outcome")) == "posted" ? AuditActions.Posted : AuditActions.PostFailed,
            _ => e.Kind,
        };
        int? version = e.Kind is SavedRequestEvents.Validated or SavedRequestEvents.PostStarted && Text(d, "version") is { } v
            ? int.Parse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : null;
        var detail = action switch
        {
            AuditActions.Validated => new JsonObject { ["state"] = Kebab(Text(d, "state")) },
            AuditActions.PostStarted => new JsonObject { ["attemptId"] = Get(d, "attemptId") },
            AuditActions.Posted => new JsonObject { ["attemptId"] = Get(d, "attemptId"), ["postId"] = Get(d, "postId") },
            // SPEC-GAP T-1106 (Q-99): exactly the keys 8.3 lists, null when absent, so the restart recovery's
            // "error" text is not carried (its outcome "unknown" and the system actor say what happened).
            AuditActions.PostFailed => new JsonObject
            {
                ["attemptId"] = Get(d, "attemptId"),
                ["outcome"] = Kebab(Text(d, "outcome")),
                ["status"] = Kebab(Text(d, "status")),
                ["postId"] = Get(d, "postId"),
            },
            AuditActions.Resolved => new JsonObject
            {
                ["outcome"] = Kebab(Text(d, "outcome")), ["note"] = Get(d, "note"), ["from"] = Kebab(Text(d, "from")),
            },
            _ => d?.DeepClone(),
        };

        return new AuditEvent(
            $"e{e.Id}", e.AtUtc, e.Actor, ActorKindOf(e.Actor), action, e.SavedRequestId, null, null, null, null, null,
            version, detail);
    }

    /// <summary>
    /// One event per change of <paramref name="v"/>, numbered by the change's 0-based position. Row external ids come
    /// from <paramref name="v"/>'s payload, except a deleted row's, which comes from <paramref name="previous"/>.
    /// </summary>
    public static IReadOnlyList<AuditEvent> FromVersion(SavedRequestVersion v, SavedRequestVersion previous, string? editNote)
    {
        var events = new List<AuditEvent>(v.Changes.Count);
        for (var i = 0; i < v.Changes.Count; i++)
        {
            var c = v.Changes[i];
            var path = c.Path ?? "";
            var row = RowPath.Match(path);
            var rest = row.Success ? row.Groups[2].Value : null;
            int? index = row.Success ? int.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture) : null;

            // The rules of 8.3, in the view's order. SPEC-GAP T-1106 (Q-99): a replace of a whole row (a row that was
            // not an object) matches no row rule and falls to "any other path", request-edited.
            var action =
                rest == "" && c.Op == "add" ? AuditActions.RowAdded
                : rest == "" && c.Op == "remove" ? AuditActions.RowDeleted
                : rest == ".post" ? (IsFalse(c.New) ? AuditActions.RowPostOff : AuditActions.RowPostOn)
                : rest is { Length: > 0 } ? AuditActions.RowEdited
                : path == "$.controlTotal" ? AuditActions.ControlTotalChanged
                : AuditActions.RequestEdited;
            var field = action switch
            {
                AuditActions.RowPostOff or AuditActions.RowPostOn => "post",
                AuditActions.RowEdited => rest!.StartsWith('.') ? rest[1..] : rest,
                AuditActions.ControlTotalChanged => "controlTotal",
                AuditActions.RequestEdited => RootPrefix.Replace(path, "", 1) is { Length: > 0 } f ? f : null,
                _ => null,
            };
            var isRow = action.StartsWith("row-", StringComparison.Ordinal);
            var externalId = isRow
                ? ExternalIdAt(action == AuditActions.RowDeleted ? previous.Payload : v.Payload, index!.Value)
                : null;

            events.Add(new AuditEvent(
                $"v{v.Id}-{i}", v.CreatedUtc, v.CreatedBy, ActorKindOf(v.CreatedBy), action, v.SavedRequestId,
                isRow ? index : null, externalId, field, c.Old?.DeepClone(), c.New?.DeepClone(), v.Number,
                new JsonObject { ["note"] = editNote }));
        }

        return events;
    }

    /// <summary>
    /// A <c>user_audit</c> row. <c>refresh-reuse</c> keeps only <c>familyId</c>; a kind not in 8.3 becomes
    /// <c>user-other</c> with <c>{ kind }</c>.
    /// </summary>
    public static AuditEvent FromUserAudit(UserAuditEntry u)
    {
        string action;
        JsonNode detail;
        if (u.Kind == RefreshReuse)
        {
            action = AuditActions.UserSessionRevoked;
            detail = new JsonObject { ["familyId"] = Get(u.Detail, "familyId") };
        }
        else if (UserActions.TryGetValue(u.Kind, out var known))
        {
            // SPEC-GAP T-1106 (Q-99): 8.3 does not say what detail a known kind carries. The stored detail is kept and
            // the subject is added as userId, since the actor is often someone else (an admin) and nothing else in
            // the feed row names the user the entry is about.
            action = known;
            var o = u.Detail is JsonObject stored ? (JsonObject)stored.DeepClone() : new JsonObject();
            o["userId"] = u.UserId;
            detail = o;
        }
        else
        {
            action = AuditActions.UserOther;
            detail = new JsonObject { ["kind"] = u.Kind };
        }

        return new AuditEvent(
            $"u{u.Id}", u.AtUtc, u.Actor, ActorKindOf(u.Actor), action, null, null, null, null, null, null, null, detail);
    }

    /// <summary><c>NotPosted</c> → <c>not-posted</c>; text already in kebab-case is unchanged.</summary>
    public static string? Kebab(string? text) =>
        text is null ? null : WordBoundary.Replace(text, "$1-$2").ToLowerInvariant();

    // PostgreSQL's ->: the member of an object, a copy; null when not an object or no such member.
    private static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;

    // PostgreSQL's ->>: a string member's text, another member's JSON text, null for JSON null or no member.
    private static string? Text(JsonNode? node, string key) =>
        Get(node, key) switch
        {
            null => null,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            var other => other.ToJsonString(),
        };

    private static bool IsFalse(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.False;

    private static string? ExternalIdAt(JsonElement payload, int index)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("transactions", out var rows)
            || rows.ValueKind != JsonValueKind.Array
            || index >= rows.GetArrayLength())
        {
            return null;
        }

        var row = rows[index];
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("externalId", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Null => null,
            _ => id.GetRawText(),
        };
    }
}
