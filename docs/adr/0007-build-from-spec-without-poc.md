# ADR-0007: Build Core from the spec; the POC source is unavailable

**Date**: 2026-09-16
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

plan.md T-002 and T-003 say to port about 12 files and the sample job from `poc/QbAutopost`, but that code isn't available. Several spec items are defined only by pointing at the POC:
- the CSV layout format (`CsvLayouts`)
- the running-balance check in "both orientations"
- the `BatchEnterSheet` columns
- `RegexSpecParser`
- `Normalize(description)`
- the base `rules.json` schema
- the sample CSVs ("7 and 4 rows")

## Decision

We write these components from scratch, following the spec. Anything the spec leaves undefined (layout format, sheet columns, normalisation rules, base rules.json keys, sample data) gets the most conservative choice. Each such choice is marked `// SPEC-GAP T-xxx` in the code and logged in `tracker.md › Questions` for the owner to confirm.

The sample job is synthetic: a bank CSV with 7 rows (account ending 4521) and a card CSV with 4 rows (account ending 7788), matching the FR-3 acceptance counts. No `poc/` folder is created.

## Alternatives Considered

### Alternative 1: Wait for the POC
- **Pros**: A faithful port.
- **Cons**: Blocks all work.
- **Why not**: The owner chose to proceed from the spec.

## Consequences

### Positive
- Work can start now, and every invented behaviour is explicit and open to review.

### Negative
- Behaviour may differ from the POC the spec author had in mind, and golden files may need to be regenerated later.

### Risks
- The guessed bank CSV layouts may be wrong. Mitigation: layouts are data in `rules.json`, not code, and the shadow week (T-803) will expose differences.
