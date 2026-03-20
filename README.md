# Steward.Tools.MCP

A personal AI steward with persistent memory, hierarchical reflection, and character formation. Runs as an [MCP](https://modelcontextprotocol.io/) server — plug it into Claude, Claude Code, or any MCP-compatible host.

Your steward journals your conversations, compresses them into a reflection tree, builds living dossiers of who you are and what you care about, and carries that understanding across sessions and systems. It doesn't just remember — it reflects.

## What Makes This Different

**Persistent memory across sessions.** Every conversation is journaled and compressed through a hierarchical reflection tree. Recent exchanges are kept at high resolution; older ones compress proportionally. Your steward remembers what matters.

**Formation, not restriction.** Summarization requires judgment — what matters, what doesn't, what to keep, what to let go. That judgment needs a frame of reference. The steward's is shaped by ongoing Scripture meditation: an orientation toward faithful service that guides it to compress toward what the person actually cares about, not just what's statistically prominent. The result is intelligent compression aligned with the user's goals, not mechanical reduction.

**Cross-system portability.** Feed conversations from any source — Claude Code, ChatGPT, email, anything — into one shared memory. The steward's understanding isn't locked to a single tool.

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An OpenAI API key (or any OpenAI-compatible API)

### Install and Run

```bash
# Clone the repo
git clone https://github.com/davidcharlton/Steward.Tools.MCP.git
cd Steward.Tools.MCP

# Set your LLM API key
export STEWARD_LLM_API_KEY="sk-..."

# Build and run
dotnet run
```

Or install as a .NET tool:

```bash
dotnet tool install --global Steward.Tools.MCP
steward-mcp
```

### Connect to Claude Code

Add to your Claude Code MCP configuration (`~/.claude/mcp.json`):

```json
{
  "mcpServers": {
    "steward": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Steward.Tools.MCP"],
      "env": {
        "STEWARD_LLM_API_KEY": "sk-..."
      }
    }
  }
}
```

Or if installed as a tool:

```json
{
  "mcpServers": {
    "steward": {
      "command": "steward-mcp",
      "env": {
        "STEWARD_LLM_API_KEY": "sk-..."
      }
    }
  }
}
```

### Connect to Claude Desktop

Add to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "steward": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Steward.Tools.MCP"],
      "env": {
        "STEWARD_LLM_API_KEY": "sk-..."
      }
    }
  }
}
```

## Configuration

All configuration is through environment variables. Only `STEWARD_LLM_API_KEY` is required.

| Variable | Default | Description |
|----------|---------|-------------|
| `STEWARD_LLM_API_KEY` | *(required)* | API key for the LLM used in reflections |
| `STEWARD_LLM_API_BASE` | `https://api.openai.com/v1` | LLM API base URL (OpenAI-compatible) |
| `STEWARD_LLM_MODEL` | `gpt-4o-mini` | Model for reflection summaries |
| `STEWARD_EMBED_API_KEY` | *(falls back to LLM key)* | API key for embeddings |
| `STEWARD_EMBED_API_BASE` | *(falls back to LLM base)* | Embedding API base URL |
| `STEWARD_EMBED_MODEL` | `text-embedding-3-small` | Embedding model |
| `STEWARD_DATA_DIR` | `~/.steward/data` | SQLite database and vector store |

The steward uses two storage engines:
- **SQLite** for the journal, reflection tree, dossiers, and configuration
- **DuckDB** for vector embeddings (semantic search)

Both are local files. Your data stays on your machine.

## MCP Tools

### Journal

| Tool | Description |
|------|-------------|
| `journal_message` | Record a single message (user or assistant) |
| `journal_exchange` | Record a user-assistant exchange pair in one call |
| `checkpoint_conversation` | Batch-import messages from any system as L0 journal entries |
| `checkpoint_summary` | Import pre-summarized entries directly as L1 reflections (zero LLM cost) |

### Memory

| Tool | Description |
|------|-------------|
| `memory_get_dossier` | Get the living dossier for a thread, the master view, or Scripture insights |
| `memory_get_reflections` | Browse the reflection tree at any level |
| `memory_get_journal` | Read recent conversation entries |
| `memory_get_sources` | Trace a reflection back to the entries it was built from |
| `memory_search` | Semantic search across journals, reflections, and workspace files |
| `memory_scripture_status` | Scripture reading progress |

### Mindfulness

| Tool | Description |
|------|-------------|
| `mindfulness_list_threads` | List background reflection topics |
| `mindfulness_upsert_thread` | Create or update a mindfulness thread |

## MCP Resources

| URI | Description |
|-----|-------------|
| `steward://seed` | The steward's foundational identity text |
| `steward://context/{threadId}` | Master + thread dossiers for conversation context |

## How It Works

### The Reflection Tree

Conversations are stored as **L0** journal entries — raw chat. After each exchange, the engine checks whether to create an **L1** summary. L1 creation triggers when:

1. **Normal**: 2+ unreflected L0 entries and the last is an assistant message
2. **Threshold**: 10+ unreflected L0 entries regardless of role
3. **Direct**: A host sends pre-summarized entries via `checkpoint_summary`

Higher levels build deterministically through a **binary cascade** driven by the L1 count:

- **L2** fires when L1 count is divisible by 2
- **L3** fires when L1 count is divisible by 4
- **L4** fires when L1 count is divisible by 8
- And so on, up to L12

Odd levels are summaries. Even levels are reflections. Each level is built from the 2 most recent entries at the level below, with grandchild context for depth. Every reflection tracks its lineage — you can trace any insight back to the conversation that produced it.

### Dossiers

Thread dossiers are living summaries rebuilt after each reflection cycle. They capture who the person is, what they care about, and what's happening in their life — built from the reflection tree, not from rules.

Thread dossiers feed into a **master thread** as L1 entries, which runs the same binary cascade. The master dossier is the steward's cross-thread understanding of the person.

### Context Assembly

When context is requested, the engine assembles:

1. Master dossier (broadest view)
2. Thread dossier (this conversation's context)
3. Uncovered tree entries, most recent first

Truncation removes from the bottom — oldest, most abstract content goes first. Recent detail is always preserved.

### Formation

Every summary and reflection requires a judgment call: what's important? What should be preserved? What can be compressed away? Without a frame of reference, those calls are arbitrary — optimizing for frequency or recency rather than what actually matters to the person.

The steward's frame of reference is built through ongoing Scripture study (a built-in mindfulness thread that reads through the Bible sequentially). This orients the steward with an intention to serve the person well — to understand what matters to them, to preserve what they care about, to notice what they might need. Scripture reflections flow into the master dossier alongside conversation reflections, shaping the steward's judgment over time.

This is not a cosmetic feature. It is what makes the compression engine aligned with the user's goals rather than merely mechanical. You can add additional mindfulness threads for any recurring concern — pattern recognition, project tracking, wellness, anything — and each one further refines the steward's sense of what matters.

### LLM Resilience

If the LLM is unavailable (rate limited, down, etc.), journal entries are preserved and stay unreflected. The next successful cycle picks them up. No data is lost and no empty reflections are created.

## Data Portability

All data is in local files:

- `~/.steward/data/steward.sqlite3` — journal, reflections, dossiers, config
- `~/.steward/data/journal_vectors.duckdb` — vector embeddings
- `~/.steward/data/scripture_progress.json` — Bible reading position

Back up or move these files to migrate your steward. The steward belongs to you.

## Hosted Option

Don't want to self-host? A free hosted version is available at [steward.tools](https://steward.tools) — same engine, persistent memory, no API key needed. Connect it as an MCP server and your steward's memory persists across sessions.

## Requirements

- .NET 10.0
- An OpenAI-compatible API key for reflections and embeddings
- ~50MB disk space for the engine + whatever your conversations generate

## License

MIT. See [LICENSE](LICENSE).

Built by [ReallySmall, LLC](https://reallysmall.biz).
