# Configuration

Inferpal is configured through the **Settings** window (gear icon / **Inferpal Settings**
command). A few values are also settable from the chat via slash commands (`/model`,
`/hardware`, `/tools`). This page documents every setting and the underlying config key.

## Settings window

The Settings window has seven pages, listed on the left, with a search box above them that keeps the
pages holding a matching setting. Visual Studio and VS Code show the same pages, with the same words:
both read them from one description in the Core. Each field has a short hint under it; the footer
counts the unsaved changes, and **Cancel** puts the form back as it was saved.

| Page | Contains |
|---|---|
| **Server and models** | Server address and **Test**, chat model, autocomplete model, code search model, context window; **Suggest the best models** (picks among the installed models from Inferpal's measurements, fills the form, saves nothing); **Loaded now** (the models in memory, what each is used for, its memory, context and when it unloads, with **Unload** and **Unload all**). *Show advanced settings* adds: a model per task (agent, explain/fix/refactor, edit with AI, background tasks, `/bench` routing), sampling, your GPU (graphics memory, unloading idle models), connection (server type, API key) |
| **Agent and approvals** | Plan before acting, check the build after each edit, steps per request; *Ask me before changing files, running commands or going online*, with the number of approval rules; custom instructions, language persona; time limits (folded) |
| **Context and memory** | Long conversations (summarizing, turns kept whole, session recap, summary time limit, opening messages kept as is); pinned files |
| **Code search** | Background indexing, relevant code added to each question, language servers, results per search, minimum similarity |
| **Autocomplete** | Ghost text and its speed; the change shown before applying it (*Edit with AI*) |
| **Tools and MCP servers** | MCP servers as cards that say whether each one runs, and why not; the approval rules as a table, team file first, each rule saying whether it applies; your slash commands; your agent tools. Every list keeps an *Edit as text* (or JSON) view |
| **Language and appearance** | Interface language (independent of the editor's), the theme in use (Inferpal follows the editor's, high contrast included), density, agent steps shown open |

The *Show advanced settings* box only folds: it never changes a value, and it opens by itself when a
model per task or the sampling setting is no longer at its factory value — the settings that change,
without a word on screen, which model answers or how are never hidden.

> [!TIP]
> The system prompt is rebuilt before every question: an edit to the custom system prompt or to
> `.inferpal/context.md` applies from the next one — no need to clear the conversation.

> [!NOTE]
> **Visual Studio, VS Code and every open window share one `config.json`.** A save writes only the settings that
> window changed, and the lists — pinned files, approval rules, your slash commands and agent tools, MCP servers,
> documentation sources — are merged item by item: a file pinned in one window and an MCP server added in another
> both stay. An item both windows changed keeps the last save. A value Inferpal fills in by itself (the graphics memory
> it detects, the model it picks on a first start) never replaces one set in another window meanwhile.

## Config key reference

Every persisted setting, its type, and default value.

### Connection

| Key | Type | Default | Description |
|---|---|---|---|
| `language` | string | `""` | UI language (BCP-47, e.g. `fr` or `fr-FR`); empty = follow the editor's language |
| `provider` | string | `"ollama"` | Backend: `ollama` / `lmstudio` / `openai-compatible` |
| `baseUrl` | string | `"http://localhost:11434"` | Model server URL |
| `apiKey` | string | `""` | API key for OpenAI-compatible servers (Bearer). ⚠ Stored **in clear text** in this file, unlike MCP OAuth tokens, which are encrypted (`mcp-oauth.dat`, DPAPI or the editor's keychain). The file sits under your user profile with the usual ACLs, but anything that can read your `%AppData%` can read this key |
| `defaultModel` | string | `"llama3.1"` | Main chat model. Left at this default when the server does not have it, the best installed chat model is used instead, saved here, and a notice says so; a model you chose is never replaced |
| `codeActionsModel` | string | `""` | Model for Explain, Review, Fix, Refactor and Doc, in both editors (empty = the chat model) |
| `inlineCompletionModel` | string | `""` | Dedicated FIM model (empty = `defaultModel`) |
| `inlineEditModel` | string | `""` | Inline Edit model (fallback: `codeActionsModel` → `defaultModel`) |
| `agentModel` | string | `""` | AgentOrchestrator model (empty = `defaultModel`) |
| `utilityModel` | string | `""` | Model for background utility tasks — session titles, `/commit` message proposals, compaction summaries (empty = `defaultModel`). Optional: the compaction summary replaces the turns it covers, and small models keep fewer of its facts — see [Models](models.md#by-graphics-card). Resolution is centralized in `ModelRouter` |
| `modelRouterAuto` | bool | `false` | Model Router auto mode: when no `utilityModel` is set, utility tasks use the model `/bench` recommended for the utility role — but only if it is already warm in VRAM (a cold model is never loaded for a title or commit message; falls back to the chat model). An explicit `utilityModel` always wins |
| `ragEmbeddingModel` | string | `""` | Embedding model — optional. Empty = the best embedding model installed (EmbeddingGemma first); none installed = keyword search only |

### Behavior & safety

| Key | Type | Default | Description |
|---|---|---|---|
| `commandTimeoutSeconds` | int | `120` | `run_command` timeout (seconds) |
| `toolBubblesExpanded` | bool | `false` | Show each step of an agent run open (its arguments and output). Off: one line per step, opened on a click; a step that failed always opens. Either way, a run's steps show while it works and fold into one line when it ends |
| `chatDensity` | string | `"comfortable"` | `comfortable` or `compact`: how much space the chat leaves around its turns. Compact fits more of the conversation in a narrow panel |
| `securityAlertsDisabled` | bool | `false` | Auto-approve the calls that would otherwise prompt (the built-in catastrophic-command denylist still applies). Shown inverted in the Settings window: *Ask me before changing files, running commands or going online* |
| `permissionRules` | string | `""` | Allow/deny rules, one per line: `allow\|deny <tool\|*> <regex>` (see [Tools → Permission rules](tools.md)) |
| `smartFixEnabled` | bool | `true` | Auto build/typecheck after `write_file`/`apply_diff`/`apply_edits` — .NET / TypeScript / Rust / Go (see workspace overlays) |
| `inlineDiffPreviewEnabled` | bool | `true` | Inline diff preview for in-place code actions (`/fix` `/refactor` `/doc`): per-hunk ✓/✗ accept/reject in the editor (VS adornment; native Refactor Preview in VS Code) instead of an immediate rewrite. Falls back to direct apply when no renderer is available; orthogonal to tool approval |

### Inline completions

| Key | Type | Default | Description |
|---|---|---|---|
| `inlineCompletionEnabled` | bool | `true` | Enable ghost-text completions |
| `inlineCompletionMode` | string | `"Default"` | FIM speed: `Fast` / `Default` (shown *Balanced*) / `HighAccuracy` (shown *Accurate*) |

### RAG / semantic index

| Key | Type | Default | Description |
|---|---|---|---|
| `ragEnabled` | bool | `true` | Semantic indexing: the workspace is indexed at startup and searched hybrid — cosine + BM25 lexical fused with RRF. `false` = no index at startup (`search_codebase` is then not offered); `/index rebuild` still builds a keyword-only one |
| `ragAutoContextEnabled` | bool | `true` | Silently inject the most relevant indexed chunks into each code-related turn (skips already-attached files). Needs `ragEnabled`; `/xray` says when it is switched on but cannot run |
| `ragTopK` | int | `5` | Chunks returned by `search_codebase` (1–10) |
| `ragSimilarityThreshold` | float | `0.20` | Minimum cosine score to keep a chunk (vector side) |
| `lspEnabled` | bool | `false` | LSP semantic chunking (TS/JS/Python/Go/Rust) |

### Context & memory

| Key | Type | Default | Description |
|---|---|---|---|
| `contextWindowSize` | int | `8192` | `num_ctx` + client token budget. 0 = the model's default: the window LM Studio, vLLM or llama-server report loading is then the budget; with Ollama, which reports none, trimming is off |
| `useRecommendedSampling` | bool | `true` | Send the sampling settings the model's vendor recommends, for the families in [Models](models.md) (`false` = the server's own settings) |
| `contextWindowKeepTurns` | int | `4` | Recent turns to keep when trimming — at most: turns too long to fit next to the tool definitions are summarized with the rest |
| `compactionEnabled` | bool | `true` | Summarize old messages (LLM) instead of hard truncation |
| `compactionTimeoutSeconds` | int | `45` | Compaction safety fuse: seconds the summary may go without writing anything (its reasoning counts), on top of the time needed to read what it summarizes (about 1 s per 100 tokens); past it, the older turns are dropped instead. A model that keeps writing, or reasoning, is waited for |
| `kvCacheAnchorMessages` | int | `3` | First N messages kept verbatim so the backend can reuse its KV cache |
| `oodaTurnThreshold` | int | `10` | Turns before an OODA recap (0 = off) |
| `customSystemPrompt` | string | `""` | Appended to the base system prompt |
| `pinnedContextFiles` | string | `""` | Up to 3 paths (`\n`-separated) always injected; also pinned and unpinned from the chat in both editors |

### Hardware & model lifetime

| Key | Type | Default | Description |
|---|---|---|---|
| `vramBudgetGb` | double | `0` | Total VRAM budget (GB). 0 = unknown → fit-checks stay silent. Auto-seeded from `nvidia-smi` on a local Ollama host |
| `modelAutoUnloadEnabled` | bool | `true` | Per-request `keep_alive` + auto-unload idle models |
| `modelIdleTimeoutMinutes` | int | `10` | Idle minutes before unloading from VRAM (min 1) |

### Timeouts (dynamic engine)

| Key | Type | Default | Description |
|---|---|---|---|
| `quickTimeoutSeconds` | int | `120` | Quick tasks (explain/review/fix/doc/inline edit/plan) |
| `normalTimeoutSeconds` | int | `300` | Per-turn timeout for chat questions, with or without tools, and the orchestrator |
| `deepTimeoutSeconds` | int | `600` | Extended-reasoning timeout. Not in the Settings window: no request uses this budget today |

These budgets wait for the model. Connecting to the backend has its own fixed budget of 15 s: a backend that never
answers the connection (machine switched off, firewall, Ollama not started under WSL's mirrored networking) is
reported as unreachable after 15 s, not after the operating system's own retries (up to two minutes on Linux).

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

Three optional files under `.inferpal/` let a team version-control policy alongside the code.
They layer on top of the per-machine settings above.

| File | Purpose |
|---|---|
| `.inferpal/permissions.json` | **Deny** rules pushed to everyone on the repo: `{ "rules": ["deny run_command ^curl", "deny * \\.env$"] }`. `allow` rules are ignored here — a cloned repository must not be able to auto-approve itself; auto-approval lives in the per-machine setting. See [Tools → Permission rules](tools.md). |
| `.inferpal/validators.json` | Per-ecosystem Smart Fix commands, keyed by extension: `{ ".ts,.tsx": { "marker": "tsconfig.json", "command": "npx tsc --noEmit" } }`. Extends/overrides the built-in .NET / TS / Rust / Go validators. ⚠ This file is committed, so it arrives with any clone: a command defined here is **always shown for approval before it runs** (asked once per session, and no `allow` rule, nor unticking *Ask me before changing files, running commands or going online*, can auto-approve it). The built-in validators are unaffected. |
| `.inferpal/project.json` | **Project profile** — how a repository likes to be worked on. `/onboard init` writes a commented example; `/onboard` shows what it asked for and what it got. |

When it indexes a git repository, Inferpal adds only `.inferpal/history/` — its local snapshots of
the files the agent overwrote — to `.gitignore`, so these overlays (and `rules/`, `checks/`,
`prompts/`) stay committable. Earlier versions ignored the whole folder; that block is narrowed the
next time the repository is indexed. A rule you wrote yourself is never changed. The history folder
also carries its own `.gitignore`, so its snapshots stay out of `git status` whatever the layout — a
solution below the repository root, or indexing turned off. Snapshot names end in `.bak`, so no test
runner or compiler mistakes a backup for the file it copies.

### The project profile (`.inferpal/project.json`)

```jsonc
{
  // Applied automatically — additive only.
  "indexExclude": ["vendor", "**/*.generated.cs"],

  // Shown by `/onboard`, applied only by `/onboard apply`.
  "recommend": {
    "agentModel": "devstral-small-2:24b",
    "ragEmbeddingModel": "embeddinggemma",
    "contextWindowSize": 16384
  }
}
```

Comments and trailing commas are accepted. A file that cannot be parsed or opened applies nothing,
and `/onboard` says so — naming the file and the parser's error — rather than reporting no profile.
The file is **non-privileged** — it ships with
whatever you clone, so it may describe preferences and may never grant anything. Exactly three
categories, and the classification is an allow-list: **an unknown key is ignored, not
interpreted.**

| Category | Keys | What happens |
|---|---|---|
| Applied | `indexExclude` | Kept out of the semantic index, **additively**: the profile can exclude more, never include more. It shrinks the index only — nothing is hidden from `read_file`, `list_files` or `search_in_files`, and `/onboard` prints every pattern. |
| Recommended | `defaultModel`, `agentModel`, `utilityModel`, `codeActionsModel`, `inlineEditModel`, `inlineCompletionModel`, `ragEmbeddingModel`, `contextWindowSize` (under `recommend`) | Displayed next to the value currently in effect. Nothing changes until you type `/onboard apply`: which models are installed and how much VRAM you have are machine facts, not repository facts. A `contextWindowSize` is applied when it is `0` (the model's own window) or from 512 to 1,000,000; any other value is named and left aside. |
| Never | everything else — `validators`, `permissions`, `baseUrl`, `apiKey`, `customTools`, … | Ignored, and named in the `/onboard` report (⛔) plus `/diagnostics`. Nesting one of them under `recommend` does not launder it. |

Nothing here raises an approval prompt, because nothing here executes: a file that cannot grant
anything has no approval to ask for. Compare `validators.json` above, which *can* name a command
and is therefore force-prompted.

(Existing overlays — `.inferpal/context.md`, `memory.md`, `notes.md`, `rules/`, `checks/`,
`prompts/` — are unchanged; see [Rules & Checks](rules-and-checks.md).)

## Related

- [Providers](providers.md) — provider-specific connection details.
- [Search & Indexing](search-and-indexing.md) — RAG settings in context.
- [Architecture → System prompt layering](architecture.md#system-prompt-layering).
