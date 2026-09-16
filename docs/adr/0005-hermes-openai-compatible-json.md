# ADR-0005: Hermes over an OpenAI-compatible HTTP API with validated JSON

**Date**: 2026-09-15 (backfilled on 2026-09-16 from spec.md §9)
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

Four tasks need a model:
- T1: requirement text → job spec
- T2: PDF text → statement rows
- T3: invoice → invoice facts
- T4: line → account

The Hermes Agent container runs on the same server. It exposes an OpenAI-compatible endpoint on `127.0.0.1:8642` and passes calls through to a cloud provider.

## Decision

`IHermesClient.CompleteJsonAsync<T>` works like this:
1. Posts to `/v1/chat/completions` with `temperature: 0`.
2. Strips code fences, deserialises the reply into a typed record, and validates it.
3. If that fails, retries once with the validation errors appended to the message.
4. If the retry also fails, throws `HermesValidationException`. The caller then falls back (T1 uses `RegexSpecParser`) or holds the item (T2–T4).

Prompts are `.md` files, and every call is saved to `output/hermes/`. The client uses plain `HttpClient`, with no vendor SDK.

## Alternatives Considered

### Alternative 1: A vendor SDK (OpenAI or Anthropic .NET)
- **Pros**: Helpers for structured output.
- **Cons**: A new package, ties the app to one provider, and bypasses Hermes.
- **Why not**: Not on the approved package list, and Hermes is the chosen gateway.

### Alternative 2: Semantic Kernel
- **Pros**: Orchestration features.
- **Cons**: A large dependency for four simple calls.
- **Why not**: Not needed.

## Consequences

### Positive
- Works with any provider, is easy to fake (`FakeHermesClient` returns fixture files), and every call is auditable.

### Negative
- Making JSON parsing robust is our own job.

### Risks
- The model may reply with prose instead of JSON. Mitigation: fence stripping, the retry, and fallback or hold.
