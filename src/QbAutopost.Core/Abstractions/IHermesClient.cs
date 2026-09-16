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
/// Hermes (OpenAI-compatible) client (spec §9, plan §1). Implemented in M2 (T-201); tests use <c>FakeHermesClient</c>.
/// </summary>
public interface IHermesClient
{
    Task<T> CompleteJsonAsync<T>(HermesTask task, string userContent, CancellationToken ct)
        where T : IValidatable;
}
