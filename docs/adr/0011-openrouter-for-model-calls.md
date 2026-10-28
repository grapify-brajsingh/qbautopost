# ADR-0011: OpenRouter for all model calls; Hermes retired

**Date**: 2026-10-28
**Status**: accepted (supersedes the endpoint of ADR-0005; its OpenAI-compatible JSON contract still holds)
**Deciders**: Braj (owner), Claude Code

## Context

ADR-0005 sent every model call to the Hermes Agent container on `127.0.0.1:8642`. That container needs Docker (WSL 2 on
Windows Server) on the QuickBooks server, is one more thing to keep running, and sat in front of a cloud provider
anyway. Milestone 12 (spec `docs/dashboard-api-m12.md`) also adds PDF upload extraction, which needs a model that reads
documents. The owner decided on 2026-10-28 (M-5) that the whole app uses OpenRouter and Hermes is used nowhere.

## Decision

All model calls (job tasks T1 to T4 and the upload extraction) go through the one existing OpenAI-compatible client to
`https://openrouter.ai/api/v1/chat/completions`, with a bearer key from `OpenRouter__ApiKey` (environment only) and the
model `~google/gemini-pro-latest` by default. The `OpenRouter` settings section replaces `Hermes`; a leftover `Hermes`
section is ignored with one startup warning. Code type names (`IHermesClient`, `HermesClient`, `FakeHermesClient`) and
the v1 routes (`/api/v1/health/hermes`) keep their names so nothing in v1 changes (tracker Q-109 records the rename).

## Alternatives Considered

### Alternative 1: Keep Hermes for the job pipeline and add OpenRouter only for uploads
- **Pros**: Statement and invoice text stays on the server for job calls.
- **Cons**: Two clients, two keys, two failure modes, and Docker stays on the server.
- **Why not**: The owner chose one provider for the whole app (M-5).

### Alternative 2: A vendor SDK
- **Pros**: Helpers for structured output.
- **Cons**: A new package and provider lock-in.
- **Why not**: Not on the approved package list; the plain `HttpClient` already works against any OpenAI-compatible API.

## Consequences

### Positive
- No Docker, WSL or container on the QuickBooks server; one client, one key, one health check.
- The OpenAI-compatible JSON contract of ADR-0005 (`temperature: 0`, fence stripping, one retry, typed validation,
  `FakeHermesClient` in tests) is unchanged, so job behaviour and the gates are unchanged.

### Negative
- **Statement and invoice text now leaves the server** for OpenRouter and the model provider behind it. This is the
  owner's explicit decision (M-5, 2026-10-28). Amounts are still never computed by the model (ADR-0004).
- The server needs outbound HTTPS to `openrouter.ai`, and each call and the health ping cost a small amount.

### Risks
- A leaked key. Mitigation: the key lives only in an environment variable, is scrubbed from logs and audit copies, and
  `BaseUrl` must be `https://` (or loopback) so it is never sent in clear text.
- OpenRouter or the network is down. Mitigation: unchanged from before; the job holds or falls back (T1 uses
  `RegexSpecParser`), and `OpenRouter:Enabled=false` holds every model call.
