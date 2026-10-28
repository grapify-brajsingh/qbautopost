# Architecture Decision Records

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [0001](0001-cross-platform-core-com-isolated.md) | Cross-platform Core; QuickBooks COM isolated in its own project | accepted | 2026-09-15 |
| [0002](0002-json-files-no-database.md) | JSON files and job folders as the only state store | superseded in part by ADR-0009 | 2026-09-15 |
| [0003](0003-single-in-process-worker.md) | Single in-process job worker (Channel + BackgroundService) | accepted | 2026-09-15 |
| [0004](0004-model-never-computes-money.md) | The model never computes money; deterministic gates decide what posts | accepted | 2026-09-15 |
| [0005](0005-hermes-openai-compatible-json.md) | Hermes over an OpenAI-compatible HTTP API with validated JSON | accepted | 2026-09-15 |
| [0006](0006-qbsdk-interactive-session.md) | Post via QuickBooks Desktop SDK (qbXML over COM) from an interactive session | accepted | 2026-09-15 |
| [0007](0007-build-from-spec-without-poc.md) | Build Core from the spec; the POC source is unavailable | accepted | 2026-09-16 |
| [0008](0008-synchronous-validate-and-post.md) | Synchronous validate-and-post — refuse when busy, all-or-nothing on validation | proposed | 2026-09-24 |
| [0009](0009-postgresql-for-saved-requests.md) | PostgreSQL for saved requests and dashboard users | accepted | 2026-10-27 |
| [0010](0010-dashboard-users-and-jwt.md) | Dashboard users, Identity and short-lived JWTs | accepted | 2026-10-27 |
