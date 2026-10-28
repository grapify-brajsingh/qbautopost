using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// T-1106: the audit feed of the fake, built with <see cref="AuditEventMapper"/> from the fake's own events and
/// versions plus the <c>user_audit</c> rows added by <see cref="AddUserAudit"/>. Filters, order and paging are those
/// of the EF Core store over the view <c>audit_events</c>.
/// </summary>
public sealed partial class FakeSavedRequestStore : ISavedRequestAuditStore
{
    private readonly List<UserAuditEntry> _userAudit = [];
    private long _nextUserAuditId = 1;

    /// <summary>Adds a <c>user_audit</c> row (what UserAuditWriter writes in the app); an id of 0 gets the next one.</summary>
    public UserAuditEntry AddUserAudit(UserAuditEntry entry)
    {
        lock (_gate)
        {
            var stored = entry with
            {
                Id = entry.Id == 0 ? _nextUserAuditId++ : entry.Id,
                Detail = entry.Detail?.DeepClone(),
            };
            _nextUserAuditId = Math.Max(_nextUserAuditId, stored.Id + 1);
            _userAudit.Add(stored);
            return stored with { Detail = stored.Detail?.DeepClone() };
        }
    }

    public Task<PagedResult<AuditEvent>> AuditEventsAsync(AuditQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            var rows = AuditRows(query)
                .OrderByDescending(e => e.AtUtc)
                .ThenByDescending(e => e.Id, StringComparer.Ordinal)
                .ToList();
            var size = Math.Max(1, query.PageSize);
            var page = Math.Max(1, query.Page);
            IReadOnlyList<AuditEvent> items = rows.Skip((page - 1) * size).Take(size).ToList();
            return Task.FromResult(new PagedResult<AuditEvent>(items, page, size, rows.Count));
        }
    }

    public Task<int> AuditEventCountAsync(AuditQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(AuditRows(query).Count());
        }
    }

    // ---- helpers (call only inside the lock) --------------------------------------------------------------------

    private IEnumerable<AuditEvent> AuditRows(AuditQuery query)
    {
        var all = AllAuditEvents();
        if (!query.IncludeUserEvents) all = all.Where(e => !e.Id.StartsWith('u'));
        if (query.From is { } from) all = all.Where(e => e.AtUtc >= from);
        if (query.To is { } to) all = all.Where(e => e.AtUtc < to);
        if (query.Actor is { Length: > 0 } actor) all = all.Where(e => e.ActorId == actor);
        if (query.RequestId is { } requestId) all = all.Where(e => e.RequestId == requestId);
        if (query.Reference is { Length: > 0 } reference) all = all.Where(e => RequestOf(e)?.Reference == reference);
        if (query.PushId is { } pushId) all = all.Where(e => RequestOf(e)?.Metadata.PushId == pushId);
        if (query.Actions.Count > 0) all = all.Where(e => query.Actions.Contains(e.Action, StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var q = query.Q.Trim();
            all = all.Where(e => AuditMatches(e, q));
        }

        return all.ToList();
    }

    private IEnumerable<AuditEvent> AllAuditEvents()
    {
        foreach (var events in _events.Values)
        {
            foreach (var e in events)
            {
                if (AuditEventMapper.FromEvent(Copy(e)) is { } mapped) yield return mapped;
            }
        }

        foreach (var versions in _versions.Values)
        {
            var ordered = versions.OrderBy(v => v.Number).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                foreach (var mapped in AuditEventMapper.FromVersion(Copy(ordered[i]), Copy(ordered[i - 1]), ordered[i].Note))
                    yield return mapped;
            }
        }

        foreach (var u in _userAudit)
        {
            yield return AuditEventMapper.FromUserAudit(u with { Detail = u.Detail?.DeepClone() });
        }
    }

    private SavedRequest? RequestOf(AuditEvent e) =>
        e.RequestId is { } id && _requests.TryGetValue(id, out var r) ? r : null;

    private bool AuditMatches(AuditEvent e, string q)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        var r = RequestOf(e);
        return (e.Old is not null && JsonbText(e.Old).Contains(q, ci))
            || (e.New is not null && JsonbText(e.New).Contains(q, ci))
            || (e.Field?.Contains(q, ci) ?? false)
            || (r?.Reference?.Contains(q, ci) ?? false)
            || (r?.PublicRef.Contains(q, ci) ?? false)
            || (r?.Metadata.PushId?.ToString("N").Contains(q, ci) ?? false);
    }

    /// <summary>
    /// A JSON value as PostgreSQL prints a jsonb (<c>::text</c>), so <c>q</c> matches the same text in both stores:
    /// <c>", "</c> and <c>": "</c> separators, keys shorter-first then by UTF-8 bytes, non-ASCII left unescaped.
    /// </summary>
    private static string JsonbText(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();

        static void Write(StringBuilder sb, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    sb.Append("null");
                    break;
                case JsonObject o:
                    sb.Append('{');
                    var first = true;
                    foreach (var (key, value) in o.OrderBy(p => p.Key, JsonbKeyOrder.Instance))
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        WriteString(sb, key);
                        sb.Append(": ");
                        Write(sb, value);
                    }

                    sb.Append('}');
                    break;
                case JsonArray a:
                    sb.Append('[');
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        Write(sb, a[i]);
                    }

                    sb.Append(']');
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    WriteString(sb, v.GetValue<string>());
                    break;
                default:
                    sb.Append(node.ToJsonString());
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                    default: sb.Append(c); break;
                }
            }

            sb.Append('"');
        }
    }

    private sealed class JsonbKeyOrder : IComparer<string>
    {
        public static readonly JsonbKeyOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Encoding.UTF8.GetBytes(x ?? "");
            var b = Encoding.UTF8.GetBytes(y ?? "");
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.AsSpan().SequenceCompareTo(b);
        }
    }
}
