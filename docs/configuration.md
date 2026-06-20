# Configuration

Inferpal is configured through the **Settings** window (gear icon / **Inferpal Settings**
command). A few values are also settable from the chat via slash commands (`/model`,
`/hardware`, `/tools`). This page documents every setting and the underlying config key.

## Settings window

The Settings window is organized into collapsible sections:

| Section | Contains |
|---|---|
| **Connection** | Provider, Server URL, API key, Chat model, Code Actions model, FIM model, Embedding model, **Test** |
| **Behavior** | Command timeout, expand tool bubbles, disable security alerts, permission rules, Smart Fix |
| **Inline Completions** | Enable ghost text, preset (Fast / Default / High Accuracy) |
| **RAG / Semantic Index** | Enable semantic indexing, auto-inject context per turn, Top-K |
| **Context & Memory** | VRAM budget, context window, keep turns, compaction (+ timeout), OODA threshold, KV-cache anchor |
| **Persona** | Custom system prompt |
| **MCP** | Enable MCP servers, server map (JSON), per-server status |
| **Advanced (collapsible editors)** | Pinned context files, prompt templates, custom shell tools — editable as raw text |

Language is selected at the top, independently of Visual Studio's UI language.

> [!TIP]
> After editing the custom system prompt or `.inferpal/context.md`, run `/clear` to rebuild
> the system prompt.

## Config key reference

Every persisted setting, its type, and default value.

### Connection

| Key | Type | Default | Description |
|---|---|---|---|
| `language` | string | `""` | UI language (BCP-47); empty = follow Visual Studio |
| `provider` | string | `"ollama"` | Backend: `ollama` / `lmstudio` / `openai` |
| `baseUrl` | string | `"http://localhost:11434"` | Model server URL |
| `apiKey` | string | `""` | API key for OpenAI-compatible servers (Bearer) |
| `defaultModel` | string | `"llama3.1"` | Main chat model |
| `codeActionsModel` | string | `""` | Model for Explain/Fix/Refactor (empty = `defaultModel`) |
| `inlineCompletionModel` | string | `""` | Dedicated FIM model (empty = `defaultModel`) |
| `inlineEditModel` | string | `""` | Inline Edit model (fallback: `codeActionsModel` → `defaultModel`) |
| `agentModel` | string | `""` | AgentOrchestrator model (empty = `defaultModel`) |
| `ragEmbeddingModel` | string | `""` | Embedding model (empty = `nomic-embed-text`) |

### Behavior & safety

| Key | Type | Default | Description |
|---|---|---|---|
| `commandTimeoutSeconds` | int | `120` | `run_command` timeout (seconds) |
| `toolBubblesExpanded` | bool | `false` | Expand tool-call bubbles by default |
| `securityAlertsDisabled` | bool | `false` | Auto-approve the calls that would otherwise prompt (the built-in catastrophic-command denylist still applies) |
| `permissionRules` | string | `""` | Allow/deny rules, one per line: `allow\|deny <tool\|*> <regex>` (see [Tools → Permission rules](tools.md)) |
| `smartFixEnabled` | bool | `true` | Auto build/typecheck after `write_file`/`apply_diff`/`apply_edits` — .NET / TypeScript / Rust / Go (see workspace overlays) |

### Inline completions

| Key | Type | Default | Description |
|---|---|---|---|
| `inlineCompletionEnabled` | bool | `true` | Enable ghost-text completions |
| `inlineCompletionMode` | string | `"Default"` | FIM preset: `Fast` / `Default` / `HighAccuracy` |

### RAG / semantic index

| Key | Type | Default | Description |
|---|---|---|---|
| `ragEnabled` | bool | `true` | Hybrid RAG — cosine + BM25 lexical fused with RRF (false = lexical-only) |
| `ragAutoContextEnabled` | bool | `true` | Silently inject the most relevant indexed chunks into each code-related turn (skips already-attached files) |
| `ragTopK` | int | `5` | Chunks returned by `search_codebase` (1–10) |
| `ragSimilarityThreshold` | float | `0.20` | Minimum cosine score to keep a chunk (vector side) |
| `lspEnabled` | bool | `false` | LSP semantic chunking (TS/JS/Python/Go/Rust) |

### Context & memory

| Key | Type | Default | Description |
|---|---|---|---|
| `contextWindowSize` | int | `8192` | `num_ctx` + client token budget (0 = model default, trimming off) |
| `contextWindowKeepTurns` | int | `4` | Recent turns to keep when trimming |
| `compactionEnabled` | bool | `true` | Summarize old messages (LLM) instead of hard truncation |
| `compactionTimeoutSeconds` | int | `45` | Compaction safety timeout |
| `kvCacheAnchorMessages` | int | `3` | First N messages kept verbatim so the backend can reuse its KV cache |
| `oodaTurnThreshold` | int | `10` | Turns before an OODA recap (0 = off) |
| `customSystemPrompt` | string | `""` | Appended to the base system prompt |
| `pinnedContextFiles` | string | `""` | Up to 3 paths (`\n`-separated) always injected |

### Hardware & model lifetime

| Key | Type | Default | Description |
|---|---|---|---|
| `vramBudgetGb` | double | `0` | Total VRAM budget (GB). 0 = unknown → fit-checks stay silent. Auto-seeded from `nvidia-smi` on a local Ollama host |
| `modelAutoUnloadEnabled` | bool | `true` | Per-request `keep_alive` + auto-unload idle models |
| `modelIdleTimeoutMinutes` | int | `10` | Idle minutes before unloading from VRAM (min 1) |

### Timeouts (dynamic engine)

| Key | Type | Default | Description |
|---|---|---|---|
| `quickTimeoutSeconds` | int | `120` | Quick tasks (explain/fix/doc/inline edit/plan) |
| `normalTimeoutSeconds` | int | `300` | Per-turn timeout for tooled chat and the orchestrator |
| `deepTimeoutSeconds` | int | `600` | Extended-reasoning timeout |

### Agent orchestrator

| Key | Type | Default | Description |
|---|---|---|---|
| `agentModeEnabled` | bool | `false` | Enable the Plan→Act→Observe orchestrator |
| `agentMaxIterations` | int | `20` | Max Plan→Act→Observe iterations |

### Extensibility & state

| Key | Type | Default | Description |
|---|---|---|---|
| `promptTemplates` | string | `""` | User slash templates, one per line: `/name=text` (placeholder `{args}`) |
| `customTools` | string | `""` | Custom shell tools, one per line: `name=command` |
| `personaAutoSwitch` | bool | `true` | Persona adapts to the active file's language |
| `mcpEnabled` | bool | `false` | Spawn MCP servers at startup and expose their tools |
| `mcpServersJson` | string | `""` | MCP server map (Claude Desktop / Continue JSON) |
| `docSitesJson` | string | `""` | `@Docs` indexed sources (managed via `/docs`) |
| `isFirstRun` | bool | `true` | `true` until the first model discovery, then `false` |

> [!NOTE]
> There is **no** `stepModeEnabled` setting. Agent Step Mode is a non-persistent local
> toggle (`/agent-step`), reset every session.

## Workspace overlays (committable, team-shared)

Two optional files under `.inferpal/` let a team version-control policy alongside the code.
They layer on top of the per-machine settings above.

| File | Purpose |
|---|---|
| `.inferpal/permissions.json` | Permission rules pushed to everyone on the repo: `{ "rules": ["allow run_command ^dotnet", "deny * \\.env$"] }`. See [Tools → Permission rules](tools.md). |
| `.inferpal/validators.json` | Per-ecosystem Smart Fix commands, keyed by extension: `{ ".ts,.tsx": { "marker": "tsconfig.json", "command": "npx tsc --noEmit" } }`. Extends/overrides the built-in .NET / TS / Rust / Go validators. |

(Existing overlays — `.inferpal/context.md`, `memory.md`, `notes.md`, `rules/`, `checks/`,
`prompts/` — are unchanged; see [Rules & Checks](rules-and-checks.md).)

## Related

- [Providers](providers.md) — provider-specific connection details.
- [Search & Indexing](search-and-indexing.md) — RAG settings in context.
- [Architecture → System prompt layering](architecture.md#system-prompt-layering).
