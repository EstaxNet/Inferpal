# Inferpal — Local AI Agent for VS Code

Inferpal brings a **fully autonomous AI agent** into VS Code, powered by local LLMs via [Ollama](https://ollama.com), [LM Studio](https://lmstudio.ai), or any OpenAI-compatible server. Unlike simple chat assistants, Inferpal operates in a **multi-step agentic loop**: it reads files, writes code, runs builds, executes tests, browses the web — and chains these actions on its own until the task is complete.

**No account. No telemetry. With a local model server (Ollama or LM Studio), your code never leaves your machine.**

The extension bundles its own self-contained backend — **no .NET installation required**.

---

## Why Inferpal?

| | |
|---|---|
| 🔒 **Privacy** | Local-first: with Ollama or LM Studio your source code never leaves your machine — no telemetry, no usage tracking. (You can also point Inferpal at your own OpenAI-compatible server.) |
| 🐞 **It debugs** | On a failing test it attaches a **real debugger** and reads the actual exception, stack and locals — not the runner's text. Measured: **12/12 fixed against 10/12** without the capture. |
| ⚡ **Autonomous** | The agent plans and executes multi-step tasks on its own — read, fix, build, verify, repeat. |
| 💸 **Free forever** | One local model server (Ollama, LM Studio, …), zero subscriptions. Works with any open-source model. |
| 🛡️ **Safe by design** | Every file write creates an automatic snapshot. One command restores any change. |
| 🌍 **10 languages** | The UI follows your VS Code display language automatically — no extra setup. |

---

## 🧪 `/tdd` — fix until green, with a debugger

Point it at a failing suite and it iterates: run the tests, patch the code, run again, up to five
rounds. What makes it different from a retry loop is that **it debugs**: on the first red round the
failing test is re-run under the debugger — through the **Debug Adapter Protocol**, with a
`coreclr` configuration and **no `launch.json` needed** — and the real exception, call stack and
**expanded local values** at the point of failure go into the fix prompt. That is the information
the runner's text simply does not carry. On a fixed 12-case bench with a local 27B model that is
**12/12 fixed against 10/12** without the capture, and on the two cases where the text-only loop
burns all five rounds, the fix lands on round one.

Running a test under a debugger is execution, so it asks **once per run** — not once per round —
and falls back cleanly to the text-only loop when no capture is possible, **saying so** rather than
looping in silence. And because a loop whose goal is "make the tests green" has an obvious
shortcut, **rewriting a test file always prompts**, whatever your permission rules say.

## 🐞 `/debug` — runtime investigation

Instead of guessing what the running code does, the agent sets a breakpoint, starts the program
(**you approve that one action**), then reads the call stack, the locals and any expression it
needs to settle the question. Consent is per *session*, not per step, and the step budget is
finite — a loop that runs out says so instead of concluding anyway.

---

## Features

### 🤖 Agentic loop with step mode
The model operates autonomously for up to 20 turns: it picks its tools, executes them, reads the results, and iterates until the task is done. Every step is shown live as collapsible bubbles. **Agent Step Mode** lets you pause between tool calls and inspect or override each action (slash equivalents: `/agent-step` and `/resume`).

### 🔨 Smart Fix — checked after every write
**Smart Fix Protocol** runs automatically after every `write_file` / `apply_diff` / `apply_edits` — a quick build or typecheck for **.NET, TypeScript, Rust, or Go** (extendable per repository via `.inferpal/validators.json`). A write that breaks the build is caught in the same turn that made it, not three turns later.

### 🗂️ 28 built-in tools (+ MCP & custom)

| Tool | Description |
|---|---|
| `read_file` | Reads the content of a file |
| `write_file` | Writes or overwrites a file (confirmation required, automatic snapshot) |
| `apply_diff` | Find & replace in a file — exact, then whitespace-tolerant fuzzy; `occurrence` unique/first/all (snapshot, approval shows the diff) |
| `apply_edits` | **Atomic** multi-file edit — nothing written unless every edit resolves (snapshot per file, approval required) |
| `restore_file` | Restores a file from its last snapshot |
| `delete_file` | Deletes a file (confirmation required, snapshot saved before deletion) |
| `list_files` | Lists files in a folder (glob, max 300) |
| `search_in_files` | Regex search across files (max 100 results) |
| `run_command` | Executes a shell command — PowerShell on Windows, bash on Linux and macOS (confirmation required) |
| `get_diagnostics` | Runs `dotnet build`, returns MSBuild errors and warnings |
| `run_tests` | Runs `dotnet test` / `pytest` / `npm test` / `cargo test` / `go test`, returns summary and failures |
| `get_active_document` | Retrieves the active file in the editor — including your **unsaved** edits |
| `get_open_editors` | Lists all files currently open in the editor |
| `get_git_status` | `git status`, log, branches, diff summary and optional full diff |
| `get_debugger_state` | Current break state when paused: reason, exception, call stack (`file:line`), locals (also `@debugger`) |
| `get_solution_info` | Parses `.sln` and `.csproj` — projects, frameworks, packages |
| `fetch_url` | Loads a web page and returns its text content |
| `web_search` | DuckDuckGo search — returns titles, URLs and snippets |
| `insert_at_cursor` | Inserts text at the current cursor position in the active editor |
| `replace_selection` | Replaces the current selection in the active editor |
| `update_memory` | Updates `.inferpal/memory.md` — the agent's persistent memory (append / replace / clear) |
| `analyze_code` | One facade, three modes: `callgraph` (callees/callers), `impact` (blast radius), `nexus` (cross-language REST / JS Interop / SignalR bridges) |
| `search_codebase` | Semantic search across the indexed project (natural language query) |
| `search_docs` | Semantic search across external documentation indexed via `/docs` (passages + source URLs) |
| `generate_project_map` | Generates a full project map — namespace tree, types, deps, hotspots |
| `rename_symbol` | Renames a symbol project-wide (Roslyn for C#, regex fallback; dry_run first) |
| + MCP servers | Tools from any connected stdio MCP server (filesystem, GitHub, databases…) |
| + user-defined | Configure custom shell commands exposed as agent tools via Settings |

### 🔍 Semantic codebase search
Background indexing of all source files using an embedding model from the configured provider. **Hybrid search** fuses semantic cosine similarity with lexical BM25 (Reciprocal Rank Fusion), so exact identifiers and symbol/file names rank well — not just fuzzy concepts. **Shadow pre-warm** fetches results while you type — the `search_codebase` tool responds instantly. **Smart Auto-attach** suggests the top-2 relevant files as dismissable chips, and **per-turn auto-context** silently injects the most relevant chunks into each code question. Indexing automatically **pauses while you chat** and resumes afterward, so the interactive model always gets the GPU first.

### 📚 External documentation (@Docs)
`/docs add <url>` crawls an external documentation site (same-domain, up to 50 pages), embeds it, and exposes the `search_docs` tool — so the agent answers library and framework questions from the docs themselves, citing the source page and URL. The documentation index is **global** and shared across every workspace. Manage sources with `/docs list / remove / reindex`.

### 🔌 MCP client (Model Context Protocol)
Connect any **stdio MCP server** — the same servers used by Claude Desktop and Continue (filesystem, GitHub, databases, and hundreds more). Enable MCP in Settings, paste a server map, and their tools are exposed to the agent automatically as `mcp__<server>__<tool>`. Home-grown JSON-RPC client, zero extra dependencies. Every external tool call is gated by an approval prompt with an **Allow once / Always allow this tool / Cancel** choice (the "always" grant is scoped to the session, never persisted). **100% local stays 100% local** — you choose which servers to run.

### 📐 Project rules & AI checks
Two fully-local, repo-versioned governance features:
- **Rules** (`.inferpal/rules/*.md`) — markdown rules with optional frontmatter (`globs`, `alwaysApply`, `description`). Matching rules are injected into the system prompt and **re-scoped automatically to the active file**. Manage with `/rules` and `/rules init`.
- **AI Checks** (`.inferpal/checks/*.md`) — markdown review criteria. `/check [name]` has the model review your current **git diff** against them locally (reports `file:line` + severity), without anything leaving your machine. Manage with `/checks` and `/checks init`.

### ✏️ Inline ghost-text completions
Fill-in-the-Middle suggestions appear as you type in any code file, through VS Code's inline-completion API. Tab to accept, Esc to dismiss, cancellable mid-request. Three presets (Fast 128tok/300ms · Default 256tok/600ms · High Accuracy 512tok/1000ms) and an optional dedicated FIM model.

### 🎯 Code actions (editor context menu)
Right-click any selection → **Fix**, **Refactor** or **Add Docstring**, each powered by a dedicated configurable model without tool calling. The rewrite lands **directly in the editor**, re-indented to match, and is undoable with a single Ctrl+Z.

### 📝 Inline diff viewer
After every `write_file` or `apply_diff`, an LCS-based diff is shown directly in the chat bubble — added lines in green, removed in red, unchanged blocks collapsed.

### 📋 Session templates & prompt templates
`/template` loads one of 5 preconfigured session contexts (code-review, bug-hunt, architecture, refactoring, tests). Define your own reusable prompts with `{args}` placeholders via Settings.

### ⭐ Code snippet library
Star any code block to save it to a persistent library. `/snippets list/copy/delete` manages your saved snippets across sessions.

### 🧠 Smart Persona
The assistant's persona adapts automatically to the language of the active file — C#, Python, TypeScript, Go, Rust, and more.

### 💬 Chat panel
Responses stream token by token in the sidebar (**Ctrl+Alt+I**). Full Markdown rendering: headings, code blocks, lists, **bold**, *italic*, `inline code`. Copy button on every code block. Conversation search with result dimming. **Regenerate** the last reply in one click. A **real-time context & token gauge** updates live *during generation*, then snaps to the exact `prompt_eval_count + eval_count` once the run finishes, so a long generation never looks frozen.

### @ Typed mentions
Type `@` in the prompt to open a context picker and attach exactly what you mean, inline: `@file` · `@folder` · `@code` (active selection) · `@diff` (git diff) · `@problems` (live diagnostics from the Problems panel) · `@debugger` (live break state) · `@clipboard` · `@tree` · `@token`. The mention is resolved into real context the moment you send.

### 📊 Project notes & workspace context
`/note` appends timestamped notes to `.inferpal/notes.md`, automatically injected into future prompts. The first message automatically attaches workspace info and open editors as silent context.

### 📋 Session history & export
Sessions auto-saved with a 4–5 word AI-generated title. Export to `.md` / `.txt` with a statistics header (model, turns, tool calls, tokens, duration).

### 🛡️ File snapshots, multi-file restore & undo-run
Every file modification creates a snapshot under `.inferpal/history/`. After multiple writes in one agent run, a **Restore All** button rolls back everything at once. **`/undo-run`** goes further — it reverts an entire agent run (restores edited files *and* deletes files created during that run); `/undo-run list` shows the session's tracked runs.

### ⏱️ Dynamic timeout engine
Timeouts adapt to task complexity: Quick (diagnostics, short reads), Normal (code edits), Deep (multi-file refactors). All three thresholds are configurable in Settings.

### 📡 VRAM monitoring
A live badge in the header shows the models currently resident in VRAM and their usage. Idle models are auto-unloaded according to the configured `keep_alive`.

### 🔐 Safe by design — hardened
Every path-taking tool is confined to the workspace through a single `AssertUnderRoot` sandbox. Writes, diffs, deletes and renames require approval (the prompt shows the actual diff) — and so do `fetch_url` / `web_search`, the outbound channels of the *lethal trifecta*. **Permission rules** (`allow`/`deny` patterns, per-machine + committable `.inferpal/permissions.json`) auto-approve or block calls before the prompt, and a built-in **hard denylist** of catastrophic shell commands always applies. Indirect execution (`iex`, `-EncodedCommand`, `eval`, a piped interpreter, …) is **force-prompted**: no allow rule or session grant can auto-approve what text matching cannot read. Outbound fetches pass a hardened SSRF guard (blocks DNS rebinding, IPv4-mapped IPv6, `0.0.0.0/8`, loopback and private ranges, with a ReDoS-safe timeout). MCP tool calls get the same 3-way approval prompt.

### 🔗 Heartbeat & connection guard
Inferpal silently pre-flights the model server connection before every send. The Send button turns grey when the server is unreachable. Polling recovers automatically when the server comes back.

### ⚙️ Slash commands (50+)

`/clear` `/model` `/tools` `/export` `/restore` `/undo-run` `/replay` `/read` `/ls` `/grep` `/run` `/fetch` `/search-web` `/search-code` `/git` `/diff` `/context` `/xray` `/build` `/tdd` `/solution` `/map` `/index` `/commit` `/commit-exec` `/memory` `/note` `/notes` `/history` `/phistory` `/branch` `/models` `/hardware` `/bench` `/arena` `/snippets` `/template` `/prompts` `/docs` `/check` `/rules` `/checks` `/onboard` `/plan` `/task` `/debug` `/agent-step` `/resume` `/diagnostics` `/help`. Type `/` for the autocomplete popup.

### ⚙️ Settings
Language · Provider (Ollama / LM Studio / OpenAI-compatible) · Server URL · API key · Chat model · Code Actions model · FIM model · Embedding model · Command timeout · Tool bubbles · Security alerts · Permission rules · Smart Fix · Ghost-text enable/preset · Semantic indexing · Auto-context · Top-K · Pinned context files · Context window · Keep turns · Compaction · OODA threshold · KV-cache anchor · Custom system prompt · Custom agent tools · Dynamic timeouts · VRAM keep-alive · MCP servers (enable + JSON config)

**Settings and sessions are shared with the Visual Studio extension** — same configuration file, same saved conversations. Start a conversation in one editor and finish it in the other.

### 🌍 Localization
**10 languages**: English, Français, Deutsch, Italiano, Español, Русский, 日本語, 한국어, 中文 (简体), Polski. Follows your VS Code display language automatically, or can be overridden independently.

---

## Quick start

1. Install [Ollama](https://ollama.com) and pull a model: `ollama pull qwen2.5-coder`
2. Install the extension
3. Open the **Inferpal** icon in the activity bar, or press **Ctrl+Alt+I**
4. Set the server URL and select your model in Settings
5. Click **Test** to verify the connection — you're ready

> **Recommended models:** `qwen2.5-coder` for code tasks, `llama3.1` for general-purpose chat, `nomic-embed-text` for semantic search.

---

## Requirements

| | |
|---|---|
| VS Code | 1.100 or later |
| Platforms | Windows x64, Linux x64, Apple Silicon — each build ships its own backend, **no .NET installation needed** |
| Model server | Ollama (default, full hardware-aware features), LM Studio, or any OpenAI-compatible server — running locally (default: `http://localhost:11434`, configurable) or on a remote host |

### Hardware

| | RAM | GPU VRAM |
|---|---|---|
| Minimum | 8 GB | 4 GB |
| Recommended | 32 GB+ | 12 GB+ (NVIDIA RTX 3060 12 GB / 4070 or better) |

> Larger models (70B+) benefit from more VRAM. Smaller models (7B–14B) run well on integrated graphics or CPU-only setups.

---

## License

GPL-3.0 — source at [github.com/EstaxNet/Inferpal](https://github.com/EstaxNet/Inferpal).
