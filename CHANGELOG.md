# Changelog

## v0.2.0 — 2026-08-05

Engine brought current with the reference implementation.

### Added
- `consult_steward` — ask the steward a question and get its own formed response (its voice), drawing on Scripture formation, the master dossier, and semantic memory; the exchange is journaled so consulting also forms it
- `resolve_thread` — resolve a stable host-context string (e.g. a repo path) to a canonical thread id
- `memory_list_threads` — list all conversation threads the steward knows

### Fixed
- Semantic search: tolerate DuckDB returning either `Single` or `Double` for cosine similarity (search could error depending on the DuckDB build)
- Schema migration ordering: run `ALTER` before creating the index on `external_id` (init could fail on a fresh database)
- Checkpoint import: de-duplicate so re-sent batches don't double-write

### Changed
- Reflection prompts preserve agent attribution at L2 and above (better multi-source reflections)
- Reflections and dossiers tuned so the steward speaks more in its own voice
- Tool classes now take a single `UserSteward` service holder — cleaner lifetime ownership (host owns lifetime: process for stdio, cache for hosted)

## v0.1.0 — 2026-03-20

Initial public release.

### Engine
- Deterministic binary cascade reflection tree (L1 counter drives all levels)
- User-weighted, content-rich reflection prompts
- Master thread fed by thread dossiers as L1 entries
- Context assembly: dossiers + tree entries, recent-first with graceful truncation
- DuckDB vector embeddings for semantic search
- Graceful LLM failure handling — unreflected entries preserved for next cycle
- Configurable unreflected L0 threshold trigger (default 10)

### Tools
- `journal_message` / `journal_exchange` — conversation journaling
- `checkpoint_conversation` — batch-import messages from any system
- `checkpoint_summary` — import pre-summarized entries as L1 (zero LLM cost)
- `memory_get_dossier` / `memory_get_reflections` / `memory_get_journal` / `memory_get_sources` — memory introspection
- `memory_search` — semantic search across journals and reflections
- `memory_scripture_status` — Bible reading progress
- `mindfulness_list_threads` / `mindfulness_upsert_thread` — background reflection topics

### Resources
- `steward://seed` — foundational identity
- `steward://context/{threadId}` — assembled context (master + thread dossiers)

### Formation
- Built-in Scripture study mindfulness thread
- Configurable additional mindfulness threads
- Formation flows into master dossier, shaping compression judgment
