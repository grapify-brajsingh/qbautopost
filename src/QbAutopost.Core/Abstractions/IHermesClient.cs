namespace QbAutopost.Core.Abstractions;

/// <summary>Hermes prompt tasks (spec §9.1–9.4).</summary>
public enum HermesTask
{
    Spec,
    Statement,
    Invoice,
    Account,
}

/// <summary>A model answer that can check itself; an empty list means valid (spec §9).</summary>
public interface IValidatable
{
    IReadOnlyList<string> Validate();
}

/// <summary>
/// One Hermes call (spec §9: task, system prompt, user content, schema hint). <paramref name="AuditDir"/> is the job's
/// <c>output/hermes/</c> folder; every request and response is copied there (FR-17). Null means no audit copy.
/// </summary>
public sealed record HermesRequest(
    HermesTask Task,
    string SystemPrompt,
    string UserContent,
    string? AuditDir = null,
    string? SchemaHint = null);

/// <summary>Hermes (OpenAI-compatible) client (spec §9, ADR-0005); tests use <c>FakeHermesClient</c>.</summary>
public interface IHermesClient
{
    /// <summary>
    /// Returns a validated answer. Throws <see cref="HermesValidationException"/> after two invalid answers and
    /// <see cref="HermesUnavailableException"/> when Hermes cannot be reached; the caller holds or falls back.
    /// </summary>
    Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable;
}

/// <summary>Hermes gave no usable answer; callers catch this to hold or fall back.</summary>
public abstract class HermesException(string message, Exception? inner = null) : Exception(message, inner)
{
}

/// <summary>Two answers in a row failed to parse or validate (spec §9).</summary>
public sealed class HermesValidationException(HermesTask task, IReadOnlyList<string> errors)
    : HermesException($"Hermes {task} answer failed validation twice: {string.Join("; ", errors)}")
{
    public HermesTask Task { get; } = task;

    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Hermes was unreachable, timed out, or returned a non-success status or a malformed envelope.
/// SPEC-GAP T-201: spec §9 names only the validation retry, so transport failures are not retried.
/// </summary>
public sealed class HermesUnavailableException(HermesTask task, string reason, Exception? inner = null)
    : HermesException($"Hermes {task} call failed: {reason}", inner)
{
    public HermesTask Task { get; } = task;

    public string Reason { get; } = reason;
}
