using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// OpenAI-compatible chat-completions client for Hermes (spec §9, ADR-0005): <c>temperature: 0</c>, bearer key,
/// timeout, code-fence stripping, one retry with the validation errors appended, audit copies (FR-17).
/// The <see cref="HttpClient"/> should have an infinite timeout; <see cref="HermesOptions.Timeout"/> applies per call.
/// </summary>
public sealed class HermesClient(HttpClient http, HermesOptions options) : IHermesClient
{
    public async Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable
    {
        var audit = new HermesAudit(request.AuditDir, request.Task, options.ApiKey);

        var content = await SendAsync(request, request.UserContent, audit, ct);
        var answer = JsonReply.TryParse<T>(content, out var errors);
        if (answer is not null)
        {
            return answer;
        }

        content = await SendAsync(request, WithErrors(request.UserContent, errors), audit, ct);
        answer = JsonReply.TryParse<T>(content, out var retryErrors);
        return answer ?? throw new HermesValidationException(request.Task, retryErrors);
    }

    /// <summary>The user message for the retry: the original content plus why the previous answer was rejected.</summary>
    public static string WithErrors(string userContent, IReadOnlyList<string> errors)
    {
        var text = new StringBuilder(userContent)
            .Append("\n\nYour previous reply was rejected:\n");
        foreach (var error in errors)
        {
            text.Append("- ").Append(error).Append('\n');
        }

        return text.Append("Reply again with only the corrected JSON object.").ToString();
    }

    private async Task<string?> SendAsync(HermesRequest request, string userContent, HermesAudit audit, CancellationToken ct)
    {
        var systemPrompt = string.IsNullOrEmpty(request.SchemaHint)
            ? request.SystemPrompt
            : request.SystemPrompt + "\n\n" + request.SchemaHint;
        var body = JsonSerializer.Serialize(
            new ChatRequest(
                options.Model,
                0,
                [new ChatMessage("system", systemPrompt), new ChatMessage("user", userContent)]),
            JsonOptions.Default);
        var n = audit.WriteRequest(body);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, options.CompletionsUri);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            // SPEC-GAP T-201: an empty key sends no Authorization header (a local Hermes may not need one).
            if (options.ApiKey.Length > 0)
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            using var response = await http.SendAsync(message, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            audit.WriteResponse(n, text);
            if (!response.IsSuccessStatusCode)
            {
                throw new HermesUnavailableException(
                    request.Task, string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}"));
            }

            return ReadContent(request.Task, text);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var reason = $"timed out after {options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s";
            audit.WriteResponse(n, ErrorJson(reason));
            throw new HermesUnavailableException(request.Task, reason, ex);
        }
        catch (HttpRequestException ex)
        {
            audit.WriteResponse(n, ErrorJson(ex.Message));
            throw new HermesUnavailableException(request.Task, ex.Message, ex);
        }
    }

    /// <summary><c>choices[0].message.content</c>; a missing envelope is a transport failure, empty content is an invalid answer.</summary>
    private static string? ReadContent(HermesTask task, string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object)
            {
                return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : null;
            }
        }
        catch (JsonException ex)
        {
            throw new HermesUnavailableException(task, "response is not JSON", ex);
        }

        throw new HermesUnavailableException(task, "response has no choices[0].message");
    }

    private static string ErrorJson(string reason) =>
        JsonSerializer.Serialize(new { error = reason }, JsonOptions.Default);

    private sealed record ChatRequest(string Model, decimal Temperature, IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(string Role, string Content);
}
