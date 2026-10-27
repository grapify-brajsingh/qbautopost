using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.SavedRequests;

/// <summary>The outcome of reading a stored payload: a request, or path-named errors. Never both.</summary>
public sealed record PayloadRead(SyncPostRequest? Request, IReadOnlyList<string> Errors)
{
    public bool Readable => Request is not null;
}

/// <summary>
/// Reads a stored payload into a <see cref="SyncPostRequest"/> without throwing (spec §6.1). A payload that cannot
/// be bound is <i>unreadable</i>, reported as one <c>$.path: message</c> error. <c>dryRun</c> is kept: the engine
/// refuses it, not the reader.
/// </summary>
public static class SavedPayloadReader
{
    public static PayloadRead Read(JsonElement payload)
    {
        try
        {
            var request = payload.Deserialize<SyncPostRequest>(JsonOptions.Default);
            if (request is null) return Unreadable("$: payload is null");
            // An explicit null binds to a non-nullable property without an exception; it is not a request we can run.
            if ((object?)request.Transactions is null) return Unreadable("$.transactions: must be an array");
            return new PayloadRead(request, []);
        }
        catch (JsonException ex)
        {
            return Unreadable($"{ex.Path ?? "$"}: {Message(ex)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NotSupportedException)
        {
            return Unreadable($"$: {Message(ex)}");
        }
    }

    private static PayloadRead Unreadable(string error) => new(null, [error]);

    private static string Message(Exception ex)
    {
        var message = ex.Message;
        var i = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (i > 0 ? message[..i] : message).Trim();
    }
}
