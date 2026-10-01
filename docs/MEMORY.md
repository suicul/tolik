# TOLIK Memory System

## Memory Layers

### 1. Conversations
- Raw conversation history
- Linked to sessions/users
- Temporal ordering

### 2. Events
- Discrete occurrences (task completion, error, user statement)
- Source: conversation processing
- Linkable to memories

### 3. Episodic Memory
- "What happened" at a specific time
- Context-rich (who, what, where)
- Queryable by time

### 4. Semantic Memory
- Facts, knowledge, rules
- Learned from conversations and user feedback
- Confidence score, provenance

### 5. People Memory
- Per-user: preferences, personality traits, projects
- Shared family memory
- Private vs shared scope

### 6. Humor & Family Lore
- Personalized humor profiles
- Family memes, jokes, references
- Soundboard references

### 7. Environment Events
- Changes in the home/computer environment
- Trigger proactive responses

## Memory Scope

- **Private** — visible only to the user who created it
- **Shared** — visible to all household members
- **Public** — general knowledge

## Memory Lifecycle

`
Event → Relevance Filter → Memory Type → Storage → Index
`

### Provenance
Every memory item tracks:
- source — where it came from (conversation, event, explicit)
- confidence — reliability score (0.0–1.0)
- created_at — timestamp
- scope — who can see it
- type — category (fact, preference, experience)

(`expires_at` for ephemeral memories is declared but NOT implemented —
see retention below.)

## Storage
- Primary: PostgreSQL, plain relational tables (no JSONB columns, no
  full-text index, no vector extension as of baseline).
- Retrieval is exact-substring (`ILIKE %q%`) + exact `Source` match,
  ordered by confidence/created. No semantic search yet (see Track 4).
- Migration-based schema management.
- Speaker voice profiles live here too: 192-float embedding JSON,
  source `speaker.profile`, always `Private`.

## Inspect / delete (§50)

- `GET /memory/search?q=` — inspect own visible memories
  (Private only own; Shared/Public household-visible).
- `DELETE /memory/{id}` — owner-only delete from PostgreSQL and the
  in-memory mirror, audited as `memory.delete` (ALLOW owner_deleted /
  DENY not_found_or_not_owned). Unknown ids and foreign owners both return
  `404 memory_not_found` — no existence oracle.
- Only the creator may delete, in every scope: family members cannot wipe
  each other's shared memories.
- Retention today: user-initiated delete + compaction (memories older than
  3 days fold into a summary at lowered confidence; originals are marked
  `CompactedAt`, content kept for audit). Automatic expiry (`expires_at`)
  is not implemented yet — do not store data that must self-destruct.

## Learned rules loop (§21)

Closed loop: explicit user feedback → rule → future behavior.

- `POST /tasks/{id}/feedback` with a comment stores a candidate rule at
  confidence 0.6 (`user_feedback` provenance). No silent rule creation from
  ambiguous interactions.
- Repeated identical feedback strengthens the rule (+0.1 per repeat, cap 0.95)
  instead of duplicating rows.
- `GET /rules` lists the top rules (confidence ≥ 0.5) for inspection.
- `/chat` and `/voice/turn` inject the top-5 rules into the model prompt as
  preference context, explicitly marked as data rather than instructions
  (prompt-injection boundary is preserved).
