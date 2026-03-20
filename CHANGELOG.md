# Changelog

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
