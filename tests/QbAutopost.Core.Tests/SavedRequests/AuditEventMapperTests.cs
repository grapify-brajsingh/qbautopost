using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Core.Tests.SavedRequests;

/// <summary>T-1106: one test per row of the audit mapping table (spec saved-requests-dashboard-v2 8.3).</summary>
public class AuditEventMapperTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    private static JsonNode? N(string json) => JsonNode.Parse(json);

    private static SavedRequestEvent Event(string kind, string? detail = null, long id = 7, string actor = "user-1") =>
        new(id, 42, kind, detail is null ? null : N(detail), T0, actor);

    private static SavedRequestVersion Version(int number, string payload, params PayloadChange[] changes) =>
        new(100 + number, 42, number, JsonDocument.Parse(payload).RootElement.Clone(), true, [], changes, "fix payee",
            T0.AddMinutes(number), "user-1");

    private const string Before =
        """{"reference":"R1","controlTotal":30.00,"transactions":[{"externalId":"a"},{"externalId":"b"},{"externalId":"c"}]}""";

    private const string After =
        """{"reference":"R2","controlTotal":30.00,"transactions":[{"externalId":"b"},{"externalId":"c"},{"externalId":"d"}]}""";

    private static AuditEvent One(PayloadChange change, string? note = "fix payee") =>
        Assert.Single(AuditEventMapper.FromVersion(Version(2, After, change), Version(1, Before), note));

    private static UserAuditEntry User(string kind, string? detail = null) =>
        new(9, "user-2", "user-1", kind, detail is null ? null : N(detail), T0);

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(N(expected), actual), $"expected {expected}, got {actual?.ToJsonString() ?? "null"}");

    // ---- saved_request_events --------------------------------------------------------------------------------

    [Fact]
    public void Should_MapReceived_When_EventCreated()
    {
        var e = AuditEventMapper.FromEvent(Event(SavedRequestEvents.Created))!;

        Assert.Equal(AuditActions.Received, e.Action);
        Assert.Equal("e7", e.Id);
        Assert.Equal(42, e.RequestId);
        Assert.Null(e.Detail);
    }

    [Fact]
    public void Should_MapFlaggedDuplicate_When_EventFlaggedDuplicate()
    {
        var e = AuditEventMapper.FromEvent(Event(SavedRequestEvents.FlaggedDuplicate, """{"of":3,"reasons":["same-canonical-body"]}"""))!;

        Assert.Equal(AuditActions.FlaggedDuplicate, e.Action);
        AssertJson("""{"of":3,"reasons":["same-canonical-body"]}""", e.Detail);
    }

    [Fact]
    public void Should_MapValidatedWithKebabState_When_EventValidated()
    {
        var e = AuditEventMapper.FromEvent(Event(SavedRequestEvents.Validated, """{"version":2,"state":"Held"}"""))!;

        Assert.Equal(AuditActions.Validated, e.Action);
        Assert.Equal(2, e.VersionNumber);
        AssertJson("""{"state":"held"}""", e.Detail);
    }

    [Fact]
    public void Should_MapPostStarted_When_EventPostStarted()
    {
        var e = AuditEventMapper.FromEvent(Event(SavedRequestEvents.PostStarted, """{"attemptId":5,"version":3}"""))!;

        Assert.Equal(AuditActions.PostStarted, e.Action);
        Assert.Equal(3, e.VersionNumber);
        AssertJson("""{"attemptId":5}""", e.Detail);
    }

    [Fact]
    public void Should_MapPosted_When_PostFinishedOutcomePosted()
    {
        var e = AuditEventMapper.FromEvent(Event(
            SavedRequestEvents.PostFinished, """{"attemptId":5,"outcome":"Posted","status":"Posted","postId":"P-1"}"""))!;

        Assert.Equal(AuditActions.Posted, e.Action);
        AssertJson("""{"attemptId":5,"postId":"P-1"}""", e.Detail);
    }

    [Fact]
    public void Should_MapPostFailed_When_PostFinishedOutcomeOther()
    {
        var e = AuditEventMapper.FromEvent(Event(
            SavedRequestEvents.PostFinished, """{"attemptId":5,"outcome":"Partial","status":"PartiallyPosted","postId":"P-1"}"""))!;

        Assert.Equal(AuditActions.PostFailed, e.Action);
        AssertJson("""{"attemptId":5,"outcome":"partial","status":"partially-posted","postId":"P-1"}""", e.Detail);
    }

    [Fact]
    public void Should_KebabCaseOutcome_When_PostFinishedDetailIsPascal()
    {
        var e = AuditEventMapper.FromEvent(Event(
            SavedRequestEvents.PostFinished, """{"attemptId":5,"outcome":"NotPosted","status":"Pending","postId":null}"""))!;

        Assert.Equal("not-posted", e.Detail!["outcome"]!.GetValue<string>());
        Assert.Equal("pending", e.Detail!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Should_MapPostFailed_When_RestartRecoveryHasNoAttemptId()
    {
        var e = AuditEventMapper.FromEvent(Event(
            SavedRequestEvents.PostFinished, """{"outcome":"Unknown","error":"the service restarted while this post was running"}"""))!;

        Assert.Equal(AuditActions.PostFailed, e.Action);
        AssertJson("""{"attemptId":null,"outcome":"unknown","status":null,"postId":null}""", e.Detail);
    }

    [Fact]
    public void Should_KeepDetail_When_EventDiscarded()
    {
        var e = AuditEventMapper.FromEvent(Event(SavedRequestEvents.Discarded, """{"reason":"Sent Twice By Mistake"}"""))!;

        Assert.Equal(AuditActions.Discarded, e.Action);
        AssertJson("""{"reason":"Sent Twice By Mistake"}""", e.Detail);
    }

    [Fact]
    public void Should_MapRestored_When_EventRestored()
    {
        var e = AuditEventMapper.FromEvent(Event(SavedRequestEvents.Restored))!;

        Assert.Equal(AuditActions.Restored, e.Action);
        Assert.Null(e.Detail);
    }

    [Fact]
    public void Should_KebabCaseOutcomeAndFrom_When_EventResolved()
    {
        var e = AuditEventMapper.FromEvent(Event(
            SavedRequestEvents.Resolved, """{"outcome":"NotPosted","note":"Checked In QuickBooks","from":"Unknown"}"""))!;

        Assert.Equal(AuditActions.Resolved, e.Action);
        AssertJson("""{"outcome":"not-posted","note":"Checked In QuickBooks","from":"unknown"}""", e.Detail);
    }

    [Fact]
    public void Should_SkipEditedEvent_When_Mapped()
    {
        Assert.Null(AuditEventMapper.FromEvent(Event(SavedRequestEvents.Edited, """{"version":2,"note":null,"changes":[]}""")));
    }

    [Theory]
    [InlineData("system", AuditActorKinds.System)]
    [InlineData("anonymous", AuditActorKinds.Anonymous)]
    [InlineData("api-client:n8n", AuditActorKinds.ApiClient)]
    [InlineData("3f1d0c9e-user", AuditActorKinds.User)]
    public void Should_ClassifyActor_When_ActorGiven(string actor, string kind)
    {
        Assert.Equal(kind, AuditEventMapper.ActorKindOf(actor));
    }

    // ---- version changes -------------------------------------------------------------------------------------

    [Fact]
    public void Should_MapRowAdded_When_WholeRowAdded()
    {
        var e = One(new PayloadChange("add", "$.transactions[2]", null, N("""{"externalId":"d"}""")));

        Assert.Equal(AuditActions.RowAdded, e.Action);
        Assert.Equal(2, e.RowIndex);
        Assert.Null(e.Field);
        Assert.Null(e.Old);
        AssertJson("""{"externalId":"d"}""", e.New);
    }

    [Fact]
    public void Should_MapRowDeleted_When_WholeRowRemoved()
    {
        var e = One(new PayloadChange("remove", "$.transactions[0]", N("""{"externalId":"a"}"""), null));

        Assert.Equal(AuditActions.RowDeleted, e.Action);
        Assert.Equal(0, e.RowIndex);
        AssertJson("""{"externalId":"a"}""", e.Old);
        Assert.Null(e.New);
    }

    [Fact]
    public void Should_MapRowPostOff_When_ChangeSetsPostFalse()
    {
        var e = One(new PayloadChange("replace", "$.transactions[2].post", N("true"), N("false")));

        Assert.Equal(AuditActions.RowPostOff, e.Action);
        Assert.Equal("post", e.Field);
        Assert.Equal(2, e.RowIndex);
    }

    [Fact]
    public void Should_MapRowPostOn_When_ChangeSetsPostTrue()
    {
        var e = One(new PayloadChange("replace", "$.transactions[1].post", N("false"), N("true")));

        Assert.Equal(AuditActions.RowPostOn, e.Action);
        Assert.Equal("post", e.Field);
    }

    [Fact]
    public void Should_MapRowEdited_When_RowFieldChanged()
    {
        var e = One(new PayloadChange("replace", "$.transactions[1].payee", N("\"Acme\""), N("\"Acme Ltd\"")));

        Assert.Equal(AuditActions.RowEdited, e.Action);
        Assert.Equal("payee", e.Field);
        Assert.Equal(1, e.RowIndex);
        AssertJson("\"Acme\"", e.Old);
        AssertJson("\"Acme Ltd\"", e.New);
    }

    [Fact]
    public void Should_KeepNestedPath_When_RowFieldIsDeep()
    {
        var e = One(new PayloadChange("replace", "$.transactions[0]['x y'].z", N("1"), N("2")));

        Assert.Equal(AuditActions.RowEdited, e.Action);
        Assert.Equal("['x y'].z", e.Field);
    }

    [Fact]
    public void Should_MapControlTotalChanged_When_PathIsControlTotal()
    {
        var e = One(new PayloadChange("replace", "$.controlTotal", N("30.00"), N("31.00")));

        Assert.Equal(AuditActions.ControlTotalChanged, e.Action);
        Assert.Equal("controlTotal", e.Field);
        Assert.Null(e.RowIndex);
        Assert.Null(e.RowExternalId);
    }

    [Fact]
    public void Should_MapRequestEdited_When_PathIsReference()
    {
        var e = One(new PayloadChange("replace", "$.reference", N("\"R1\""), N("\"R2\"")));

        Assert.Equal(AuditActions.RequestEdited, e.Action);
        Assert.Equal("reference", e.Field);
        Assert.Null(e.RowIndex);
    }

    [Fact]
    public void Should_StripOnlyDollar_When_PathIsBracketed()
    {
        var e = One(new PayloadChange("add", "$['x y']", null, N("1")));

        Assert.Equal(AuditActions.RequestEdited, e.Action);
        Assert.Equal("['x y']", e.Field);
    }

    [Fact]
    public void Should_MapRequestEdited_When_WholeRowReplaced()
    {
        // SPEC-GAP T-1106 (Q-99): 8.3 names add and remove of a whole row only; a replace falls under "any other path".
        var e = One(new PayloadChange("replace", "$.transactions[1]", N("\"x\""), N("""{"externalId":"c"}""")));

        Assert.Equal(AuditActions.RequestEdited, e.Action);
        Assert.Equal("transactions[1]", e.Field);
        Assert.Null(e.RowIndex);
    }

    [Fact]
    public void Should_UseNewVersionRowId_When_ReplaceOrAdd()
    {
        var replaced = One(new PayloadChange("replace", "$.transactions[0].payee", N("\"a\""), N("\"b\"")));
        var added = One(new PayloadChange("add", "$.transactions[2]", null, N("""{"externalId":"d"}""")));

        Assert.Equal("b", replaced.RowExternalId);
        Assert.Equal("d", added.RowExternalId);
    }

    [Fact]
    public void Should_UsePreviousVersionRowId_When_Remove()
    {
        var e = One(new PayloadChange("remove", "$.transactions[0]", N("""{"externalId":"a"}"""), null));

        Assert.Equal("a", e.RowExternalId);
    }

    [Fact]
    public void Should_CopyEditNote_When_ChangeMapped()
    {
        var events = AuditEventMapper.FromVersion(
            Version(2, After,
                new PayloadChange("replace", "$.reference", N("\"R1\""), N("\"R2\"")),
                new PayloadChange("replace", "$.controlTotal", N("30.00"), N("31.00"))),
            Version(1, Before), "typo in the reference");

        Assert.Equal(2, events.Count);
        Assert.All(events, e => AssertJson("""{"note":"typo in the reference"}""", e.Detail));
    }

    [Fact]
    public void Should_NumberIdsByOpIndex_When_VersionHasSeveralChanges()
    {
        var events = AuditEventMapper.FromVersion(
            Version(2, After,
                new PayloadChange("replace", "$.reference", N("\"R1\""), N("\"R2\"")),
                new PayloadChange("replace", "$.controlTotal", N("30.00"), N("31.00"))),
            Version(1, Before), null);

        Assert.Equal(new[] { "v102-0", "v102-1" }, events.Select(e => e.Id));
        Assert.All(events, e =>
        {
            Assert.Equal(2, e.VersionNumber);
            Assert.Equal(42, e.RequestId);
            Assert.Equal(T0.AddMinutes(2), e.AtUtc);
            Assert.Equal("user-1", e.ActorId);
            Assert.Equal(AuditActorKinds.User, e.ActorKind);
            AssertJson("""{"note":null}""", e.Detail);
        });
    }

    // ---- user_audit ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("created", AuditActions.UserCreated)]
    [InlineData("role-changed", AuditActions.UserRoleChanged)]
    [InlineData("disabled", AuditActions.UserDisabled)]
    [InlineData("enabled", AuditActions.UserEnabled)]
    [InlineData("password-reset", AuditActions.UserPasswordReset)]
    [InlineData("unlocked", AuditActions.UserUnlocked)]
    [InlineData("password-changed", AuditActions.UserPasswordChanged)]
    [InlineData("login", AuditActions.UserSignedIn)]
    [InlineData("login-failed", AuditActions.UserSignInFailed)]
    [InlineData("locked-out", AuditActions.UserLockedOut)]
    public void Should_MapUserAction_When_KindKnown(string kind, string action)
    {
        var e = AuditEventMapper.FromUserAudit(User(kind));

        Assert.Equal(action, e.Action);
        Assert.Equal("u9", e.Id);
        Assert.Null(e.RequestId);
    }

    [Fact]
    public void Should_AddSubjectToDetail_When_KindKnown()
    {
        var e = AuditEventMapper.FromUserAudit(User("role-changed", """{"from":"viewer","to":"admin"}"""));

        AssertJson("""{"from":"viewer","to":"admin","userId":"user-2"}""", e.Detail);
    }

    [Fact]
    public void Should_MapRefreshReuse_When_KindRefreshReuse()
    {
        var e = AuditEventMapper.FromUserAudit(User("refresh-reuse", """{"familyId":"f-1","ip":"10.0.0.1"}"""));

        Assert.Equal(AuditActions.UserSessionRevoked, e.Action);
        AssertJson("""{"familyId":"f-1"}""", e.Detail);
    }

    [Fact]
    public void Should_MapUserOther_When_KindUnknown()
    {
        var e = AuditEventMapper.FromUserAudit(User("mfa-enrolled", """{"ip":"10.0.0.1"}"""));

        Assert.Equal(AuditActions.UserOther, e.Action);
        AssertJson("""{"kind":"mfa-enrolled"}""", e.Detail);
    }

    [Fact]
    public void Should_ClassifyAnonymousActor_When_SignInFailed()
    {
        var e = AuditEventMapper.FromUserAudit(new UserAuditEntry(3, null, "anonymous", "login-failed", null, T0));

        Assert.Equal(AuditActorKinds.Anonymous, e.ActorKind);
        AssertJson("""{"userId":null}""", e.Detail);
    }
}
