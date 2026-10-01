# ADR-001: Memory origin backfill

Date: 2026-10-01
Status: accepted

## Context

Phase 3 adds `Origin (lived|imported|system|development)` to `tolik_memories`.
Pre-existing rows have no origin. Two options: nullable column with NULL =
"unclassified" (forces manual review of every legacy row at Genesis preflight),
or backfill default `lived` at migration time.

## Decision

Backfill legacy rows as `lived` (DB default on the new NOT NULL column).

## Why

- Production DB holds a single owner's continuous history; test data lives in
  separate `tolik_test_*` databases by design, so no test rows can leak in.
- Dev-era conversations are genuine owner history, not synthetic fixtures —
  classifying them `lived` states a fact, it does not launder test data.
- NULL-unclassified would make Genesis preflight a manual review of potentially
  hundreds of rows with zero additional signal: there is no feasible way to
  distinguish "dev talk" from "lived talk" retroactively for the same person.

## Consequences

- `origin` is trustworthy only for rows written after this migration; the
  ceremony import path (Phase 6) must write `imported` explicitly.
- Compaction summaries and daily reviews write `system` (machine-derived).
- Explicit `development` value exists for future dev-marked writes; test suites
  keep using isolated databases and never touch production origins.
