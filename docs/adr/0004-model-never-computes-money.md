# ADR-0004: The model never computes money; deterministic gates decide what posts

**Date**: 2026-09-15 (backfilled on 2026-09-16 from spec.md §8 and CLAUDE.md hard rules 3–4)
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

LLMs are good at reading statements and choosing accounts, but they can invent numbers. A wrong amount posted to the books is costly and hard to spot.

## Decision

Amounts come from exactly two sources:
- CSV/XLSX files parsed by code
- PDF rows read by the model that pass reconcile gate G1 (balance chain, row count and period checks)

Five hard gates hold back only the items that fail them:
- G1: the statement reconciles
- G2: the job spec is valid
- G3: account confidence is high enough
- G4: the line isn't a duplicate
- G5: the amount QuickBooks echoes back matches

Where the spec is silent, code holds the line instead of guessing and marks the spot with `// SPEC-GAP`. Dry run is the default.

## Alternatives Considered

### Alternative 1: Trust model output above a confidence score
- **Pros**: More lines post automatically.
- **Cons**: Money errors can slip through unnoticed.
- **Why not**: Unacceptable for accounting.

### Alternative 2: A person reviews every line
- **Pros**: The safest option.
- **Cons**: Saves no time over the manual procedure.
- **Why not**: It defeats the purpose. Gates plus a review of held lines strikes the balance.

## Consequences

### Positive
- Every posted amount traces back to a source row, and every held line carries a reason and candidate accounts.

### Negative
- Fewer lines post automatically at first, until people teach the app rules.

### Risks
- Someone may "fix" a total in code to get past a gate. Mitigation: CLAUDE.md forbids this, and gate tests catch it.
