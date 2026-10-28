using System.Globalization;
using System.Text;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>What the CSV columns need besides the event itself: the actor's name and the request's reference fields.</summary>
public sealed record AuditCsvContext(string Actor, string? Reference, string? PushId, string? SrRef)
{
    public static AuditCsvContext Of(AuditEvent e) => new(e.ActorId, null, null, null);
}

/// <summary>
/// Dashboard v2 spec 8.4: the audit feed as CSV. A UTF-8 BOM, one header line, RFC 4180 escaping, CRLF line ends; a
/// value that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR is prefixed with <c>'</c> so a
/// spreadsheet does not read it as a formula. Nothing here changes a value other than that prefix.
/// </summary>
public static class AuditCsv
{
    /// <summary>More matching rows than this answers <c>413 too-many-rows</c>.</summary>
    public const int MaxRows = 50_000;

    public const string ContentType = "text/csv; charset=utf-8";

    public static IReadOnlyList<string> Header { get; } =
        ["time_utc", "actor", "action", "reference", "push_id", "sr_ref", "row", "field", "old", "new"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>One CSV field: formula-prefixed when it starts with a formula character, quoted when it needs to be.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <param name="context">The columns that come from outside the event; the actor id and empty references when null.</param>
    public static async Task WriteAsync(
        Stream body, IAsyncEnumerable<AuditEvent> rows, CancellationToken ct,
        Func<AuditEvent, AuditCsvContext>? context = null)
    {
        await body.WriteAsync(Bom, ct);
        await WriteLineAsync(body, Header, ct);
        await foreach (var e in rows.WithCancellation(ct))
        {
            var c = context?.Invoke(e) ?? AuditCsvContext.Of(e);
            await WriteLineAsync(
                body,
                [
                    DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    c.Actor,
                    e.Action,
                    c.Reference ?? string.Empty,
                    c.PushId ?? string.Empty,
                    c.SrRef ?? string.Empty,
                    e.RowExternalId ?? e.RowIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    e.Field ?? string.Empty,
                    e.Old?.ToJsonString() ?? string.Empty,
                    e.New?.ToJsonString() ?? string.Empty,
                ],
                ct);
        }
    }

    private static async Task WriteLineAsync(Stream body, IReadOnlyList<string> fields, CancellationToken ct) =>
        await body.WriteAsync(Utf8.GetBytes(string.Join(',', fields.Select(Escape)) + "\r\n"), ct);
}
