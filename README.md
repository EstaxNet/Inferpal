<p align="center">
  <img src="Inferpal/assets/icon-256.png" alt="Inferpal" width="120" height="120">
</p>

<h1 align="center">Inferpal</h1>

> A Visual Studio 2026 extension — with a **VS Code front-end at feature parity** — that brings local LLMs — via Ollama, LM Studio, or any OpenAI-compatible server — directly into your IDE as an agentic developer assistant — with full tool calling, inline ghost-text completions, semantic codebase search, Markdown rendering, and zero cloud dependency.

<p align="center">
  <a href="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml"><img src="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml/badge.svg?branch=master" alt="CI"></a>
  <a href="https://github.com/EstaxNet/Inferpal/releases/latest"><img src="https://img.shields.io/github/v/release/EstaxNet/Inferpal" alt="Release"></a>
  <a href="https://www.gnu.org/licenses/gpl-3.0"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"></a>
  <img src="https://img.shields.io/badge/tests-3405%20passing-brightgreen" alt="Tests">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/Visual%20Studio-2026-5C2D91" alt="Visual Studio 2026">
  <img src="https://img.shields.io/badge/VS%20Code-parity-007ACC" alt="VS Code">
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs"><img src="https://img.shields.io/visual-studio-marketplace/v/EstaxNet.inferpal-vs?label=VS%20Marketplace" alt="Visual Studio Marketplace"></a>
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode"><img src="https://img.shields.io/visual-studio-marketplace/v/EstaxNet.inferpal-vscode?label=VS%20Code%20Marketplace" alt="VS Code Marketplace"></a>
</p>

---

## 📚 Documentation

Full functional and technical documentation lives in **[`docs/`](docs/README.md)**:

| | |
|---|---|
| [Getting Started](docs/getting-started.md) | [Providers](docs/providers.md) · [Configuration](docs/configuration.md) |
| [Features](docs/features.md) | [Slash Commands](docs/slash-commands.md) · [Tools](docs/tools.md) · [Mentions](docs/mentions.md) |
| [Search & Indexing](docs/search-and-indexing.md) | [MCP](docs/mcp.md) · [Rules & Checks](docs/rules-and-checks.md) · [Remote Inference](docs/remote-inference.md) |
| [Architecture](docs/architecture.md) | [Development](docs/development.md) |

---

## Features

- **Agentic loop** — the model autonomously chains tool calls (read files, write code, run commands, search the web, diagnose builds, run tests, search the codebase) to complete complex tasks
- **Two editors, one engine** — the Visual Studio extension (primary) and the **VS Code extension** both run the same `Inferpal.Core` and share the same configuration; the VS Code VSIX bundles a self-contained JSON-RPC host (`Inferpal.Host`), no .NET install required, and since 1.5.0 ships for **Windows x64, Linux x64 and Apple Silicon** (`run_command` speaks PowerShell on Windows, bash on POSIX — `sh` on a host without bash — same persisted cwd/env state, same force-prompt on indirection). Since 1.2.0 the VS Code front-end is at **feature parity**: full markdown chat, welcome screen, connection + VRAM badge, collapsible tool bubbles with "Fix with AI", live agent plan, slash autocomplete, Shift+↑/↓ prompt history, token counter + clickable context gauge (opens the X-Ray), conversation search, `.md`/`.txt` export, Ctrl+Alt+I, a 4-tab "Inferpal Settings" panel, typed @-mentions, `/plan` & `/agent-step` modes, per-turn RAG auto-context, and a webview localized in 10 languages; ~40 slash commands are served headless by the host with the same approvals and permissions (still VS-only: `/fix-build` `/setup`)
- **28 built-in tools** — file I/O, shell execution, code search, diagnostics, test runner, git status, debugger state **and debugger control**, active document access, single & **atomic multi-file** diff application, file restore, web fetching, internet search, project map, semantic rename, unified code analysis (call-graph / impact / cross-language nexus), documentation search, and more; plus user-defined shell tools **and MCP server tools**. Independent read-only tools in a turn run **in parallel**
- **Runtime investigation (`/debug`)** — instead of guessing what the running code does, the agent sets a breakpoint, starts the program (**you approve that one action**), then reads the call stack, the locals and any expression it needs to settle the question. Consent is per *session*, not per step, and the step budget is finite — a loop that runs out says so rather than concluding anyway. Works in **both editors**: Visual Studio through its own automation, VS Code through the Debug Adapter Protocol
- **Permission rules** — `allow` / `deny` patterns (per-machine setting + committable `.inferpal/permissions.json`) auto-approve or block tool calls before the prompt; a built-in **hard denylist** of catastrophic shell commands always applies, and indirect execution (`iex`, `-EncodedCommand`, `FromBase64String`, …) is **force-prompted** — no auto-approval path can cover what text matching cannot read. Blocked calls are recorded in `/diagnostics`
- **MCP client (Model Context Protocol)** — connect any stdio or Streamable HTTP MCP server (filesystem, GitHub, databases…); its tools are exposed to the agent automatically. Home-grown JSON-RPC client, zero extra dependencies, HTTP auth via static headers (`${ENV}` expansion) **or OAuth 2.1** (PKCE, dynamic client registration, refresh, tokens encrypted with DPAPI), approval prompt with an *Allow once / Always allow this tool (session) / Cancel* choice
- **@Docs — external documentation indexing** — `/docs add <url>` crawls a documentation site (same-domain), embeds it, and exposes the `search_docs` tool so the agent can answer library/framework questions from the docs themselves; stored globally, available across all solutions
- **Project rules** — `.inferpal/rules/*.md` with optional `globs` / `alwaysApply` frontmatter; matching rules are injected into the system prompt and re-scoped to the active file. `/rules [init]`
- **AI checks** — `.inferpal/checks/*.md` review criteria; `/check [name]` reviews the current git diff against them, 100% local (`file:line` + severity). `/checks [init]`
- **Project profile** — `.inferpal/project.json`, committed with the repository, describes how a project likes to be worked on: index exclusions are applied (additively — a clone can exclude more, never index more), model roles and context size are *recommended* and only written to your settings by an explicit `/onboard apply`, everything else is ignored. `/onboard` reports all three; `/onboard context` drafts `.inferpal/context.md` from the repository itself
- **Agent Step Mode** — pause the agent between tool calls to inspect or override each action; toggle with the **🦶 step button** in the input toolbar (or `/agent-step`), then click **▶ Resume** on the pause bubble (or `/resume`) to continue
- **Inline ghost-text completions** — Fill-in-the-Middle suggestions appear as you type (Tab to accept, Esc to dismiss); configurable Fast / Default / High Accuracy presets; optional dedicated FIM model
- **Semantic codebase search** — background indexing with 3-tier chunking (Roslyn → LSP → regex); **hybrid retrieval** (cosine + BM25 lexical fused with RRF); `search_codebase` tool + shadow pre-warm + Smart Auto-attach chips + **per-turn auto-context** injection; indexing yields to active chat/agent requests so the interactive model always loads first
- **Smart Fix Protocol (polyglot)** — after every `write_file` / `apply_diff` / `apply_edits`, a quick build/typecheck runs automatically (**.NET / TypeScript / Rust / Go**, extendable via `.inferpal/validators.json`); compilation errors are returned inline so the agent can fix them in the same loop
- **Inline diff viewer** — LCS-based diff shown in the chat bubble after every write/apply (added lines green, removed red, unchanged collapsed)
- **Inline diff preview for code actions** — `/fix`, `/refactor` and `/doc` show the rewrite in the editor with per-hunk accept/reject (VS: red/green adornment with ✓/✗; VS Code: native Refactor Preview) instead of applying blind; accepted hunks are a single undo step (`inlineDiffPreviewEnabled`, default true)
- **`/tdd [filter]`** — "fix until green": runs the test suite (`run_tests` auto-detects dotnet / pytest / npm / cargo / go), lets the agent patch the failing code, re-runs, up to 5 rounds — the test-side twin of `/fix-build`; writes go through the usual approval pipeline and `/undo-run` applies. **The loop debugs the failing test**: on the first red round it is re-run under the editor's debugger and the real exception, call stack and locals at the point of failure are added to the fix prompt (approval asked once per run; falls back to the text-only loop when no capture is possible). Rewriting a *test* file always prompts, whatever your permission rules say
- **`/task <objective>`** — background agent tasks: the run leaves the conversation and works while you code, one at a time and always behind interactive work on the GPU; a notice brings you back to its report when it lands. Read-only by design — a detached run explores and reports, so it never needs an approval prompt while you are typing
- **Code actions** — right-click context menu: **Explain**, **Fix**, **Refactor**, **Add Tests**, **Add Docstring** — dedicated model, tools disabled for direct answers
- **Inline Edit (Edit with AI)** — select code, press **Ctrl+Shift+I** (or **Edit with AI…** in the context menu), type an instruction, and the model rewrites the selection **directly in the editor** (re-indented to match)
- **Slash commands** (50+) — `/clear` `/model` `/tools` `/export` `/restore` `/undo-run` `/replay` `/read` `/ls` `/grep` `/run` `/fetch` `/search-web` `/search-code` `/git` `/diff` `/context` `/xray` `/build` `/fix-build` `/tdd` `/solution` `/map` `/index` `/commit` `/commit-exec` `/memory` `/note` `/notes` `/history` `/phistory` `/branch` `/models` `/hardware` `/bench` `/arena` `/setup` `/snippets` `/template` `/prompts` `/docs` `/check` `/rules` `/checks` `/onboard` `/plan` `/debug` `/agent-step` `/resume` `/diagnostics` `/help` + 6 code action commands; type `/` for autocomplete popup
- **Session templates** — `/template` loads a preconfigured context (code-review / bug-hunt / architecture / refactoring / tests)
- **Prompt templates** — define reusable prompts with `{args}` placeholders in Settings
- **Code snippet library** — ⭐ button on any code block saves it; `/snippets list/copy/delete/clear` manages the library across sessions
- **Custom agent tools** — configure shell commands in Settings and expose them as native tools in the agentic loop
- **Project notes** — `/note <text>` appends timestamped notes to `.inferpal/notes.md`; injected into future system prompts automatically
- **Prompt history** — persistent across sessions; search with `/phistory [term]`, reuse with `/phistory use <n>`
- **Ollama Model Manager** — `/models list/pull/delete/running` with streaming pull progress
- **Model bench** — `/bench [model…]` runs a local test bench of installed models: warm TTFT, tokens/s, VRAM pressure and a 5-task quality micro-eval scored by programmatic assertions (no LLM judge), with per-role recommendations (agent / utility / FIM) feeding the Model Router; `/bench last` redisplays the persisted run
- **Model arena** — `/arena <prompt>` sends the same prompt to two models (sequentially — one GPU) and shows both answers blind-labelled A/B; vote with `/arena a|b|tie` (reveals the models), cumulative local standings with `/arena stats`
- **Model Router** — a dedicated **utility model** role (`utilityModel`) handles session titles, `/commit` message proposals and compaction summaries instead of the chat model; opt-in **auto mode** (`modelRouterAuto`) uses the `/bench`-recommended utility model, but only when it is already warm in VRAM (a cold model is never loaded for a title or commit message)
- **@-mentions** — type `@` in the prompt for a typed context picker: `@file` `@folder` `@code` `@diff` `@problems` `@debugger` `@clipboard` `@tree` attach the corresponding context inline before sending
- **Live debugger awareness** — `get_debugger_state` (and the `@debugger` mention) expose the current break state (break reason, exception, call stack with `file:line`, locals of the active frame) so the agent can diagnose the failure you're debugging right now
- **VRAM monitoring** — live badge in the header showing the models currently resident in VRAM; `ModelLifetimeService` auto-unloads idle models
- **Heartbeat & connection guard** — silent pre-flight before every send; Send button goes grey when the model server is unreachable; auto-recovers
- **Dynamic timeout engine** — per-task complexity (Quick / Normal / Deep); all three thresholds configurable in Settings
- **Smart Persona Switching** — persona adapts automatically to the language of the active file
- **Workspace auto-context** — first message of every session silently attaches solution info + open editors
- **Multi-file approval pass** — after ≥2 file writes in one agent run, a **Restore All** button rolls back everything at once
- **Undo a whole run** — `/undo-run` reverts every file changed during the last agent run (restores edited files, deletes files created that run); `/undo-run list` shows the session's tracked runs
- **Run replay** — `/replay [n]` shows a post-mortem timeline of an agent run: every tool call with its target and duration, then the files it touched
- **Pinned context files** — up to 3 files always injected into the system prompt; pin the active file with the 📌 toolbar button or promote any attachment chip to a persistent gold 📌 chip (✕ to unpin). Still editable as raw paths in Settings.
- **Conversation search** — 🔍 header button + search bar; non-matching messages dimmed to 20% opacity
- **Sound notification** — audible ping when an agent run completes after more than 30 seconds
- **Custom system prompt** — editable in settings, appended to the base prompt before any project context
- **Persistent project context** — create `.inferpal/context.md` at the solution root; automatically injected into every system prompt
- **KV-cache anchor** — configurable number of messages preserved verbatim after compaction so Ollama can reuse its KV cache
- **Context compaction** — old messages are summarized by the LLM instead of being hard-truncated; configurable safety timeout
- **Targeted diagnostics fix** — "Fix with AI" button on build errors; prompt pre-filled with the errors **and the content of every affected file**
- **Real-time context & token gauge** — the header token counter and the context-window fill bar update *live during generation* (seeded from the sent prompt, then grown from streamed answer + reasoning tokens at ~4 chars/token, shown with a `~` prefix), then snap to the real `prompt_eval_count + eval_count` once the run completes — a long generation no longer looks frozen. Fill colour ramps green → amber → orange → red at 50/80/95%
- **Context X-Ray** — `/xray` (or a click on the context gauge) opens an interactive panel breaking down everything composing the system prompt: token bars per layer (base, custom, pinned, project files, scoped rules), exact content per section, per-section on/off toggles for the next turn, copy of the raw prompt, and an overhead warning when the fixed layers dominate the budget
- **Real-time streaming** — tokens appear as they are generated; Markdown is rendered once the response is complete
- **Markdown rendering** — headings (H1–H3), code blocks (selectable, copy button, Consolas), bullet & numbered lists, **bold**, *italic*, `inline code`; `<think>` tags stripped automatically
- **VS theme awareness** — automatically adapts colors to Visual Studio Light / Dark / Blue themes
- **Conversation persistence** — sessions auto-saved with AI-generated titles; reloadable from a timestamped picker
- **Conversation branching** — `/branch <n>` forks the conversation at a turn: the branch keeps turns 1..*n* and the conversation continues there, while the original is written back to disk first, exactly as it stands (under a generated name if it had never been saved). `/branch` lists the branch points and the family tree, `/branch <name>` switches. Branches are plain session files carrying a `parent` + `fork_turn` link
- **Session export** — export to `.md` or `.txt` with a statistics header (model, turns, tool calls, tokens, duration)
- **File history** — every write/apply saves a snapshot to `.inferpal/history/` (max 20 per file); `restore_file` reverts any change
- **Language override** — settings dropdown to force a UI language independently of Visual Studio's language
- **Localization** — 10 languages: English, French, Spanish, Simplified Chinese, German, Italian, Russian, Japanese, Korean, Polish
- **Security** — workspace-confined file operations (uniform `AssertUnderRoot` sandbox across every path-taking tool); write/diff/delete/rename require approval (the prompt shows the actual diff); **permission rules** (`allow`/`deny` patterns + catastrophic-command denylist — an accident guard, not a security boundary — + force-prompt on indirect execution, the approval prompt being the actual boundary); **`fetch_url` and `web_search` are approval-gated too** (they're the exfiltration channel of the lethal trifecta); **hardened SSRF guard** on outbound fetches (blocks DNS rebinding, IPv4-mapped IPv6, `0.0.0.0/8`, loopback/private ranges, with a ReDoS-safe timeout); circuit breaker on backend failures; loop detection prevents infinite agent loops
- **Context window management** — configurable token limit and number of turns to keep; auto-trims old messages with a warning when 80% is reached
- **Local-first** — with Ollama or LM Studio there's no API key, no telemetry, no cloud. Also works with any OpenAI-compatible server (an API key can be set for endpoints that require one); provider, URL and key are configurable in settings

---

## Requirements

| Requirement | Details |
|---|---|
| Visual Studio | **2026** Community / Professional / Enterprise. ⚠ The Marketplace listing shows 17.14 as its floor — since Visual Studio 2026 only the lower bound of an installation target is evaluated, and `18.0` is refused as experimental. The in-editor half (ghost text, inline-diff preview, `/tdd` debugger driver) still requires 2026. |
| .NET SDK | .NET 8 |
| Model server | [Ollama](https://ollama.com) (default — full hardware-aware features) running locally on port 11434, **or on a remote host** (see [Remote Inference Guide](docs/remote-inference.md)). **LM Studio** and any **OpenAI-compatible** server (llama.cpp, vLLM) are also supported — pick the provider in Settings |

> Tool calling is required. Any model that supports it works (e.g. `llama3.1`, `qwen2.5-coder`, `mistral-nemo`). `llama3` v1 does not.
>
> For inline completions, any model that supports FIM works (e.g. `qwen2.5-coder`, `deepseek-coder`).
>
> For semantic search, a dedicated embedding model is recommended (e.g. `nomic-embed-text`, `mxbai-embed-large`).

---

## Quick Start

### 1. Start a model server

Run any supported backend locally and load a tool-calling model. Pick whichever you already use:

- **Ollama** — `ollama serve`, then `ollama pull llama3.1` (optionally `qwen2.5-coder:7b` for completions and `nomic-embed-text` for semantic search).
- **LM Studio** — start the local server and load a model that supports tool calling.
- **Any OpenAI-compatible server** (llama.cpp, vLLM, …) — expose its `/v1` endpoint.

The backend doesn't have to run on the same machine as Visual Studio — point Inferpal at a remote host to offload inference (see step 6).

### 2. Build and install the extension

```powershell
# Debug (includes PDB in VSIX, for Attach to Process debugging)
dotnet build Inferpal/Inferpal.csproj

# Release (optimized, no symbols, warnings-as-errors)
dotnet build Inferpal/Inferpal.csproj -c Release
```

Open the generated `.vsix` from `Inferpal\bin\Debug\net8.0-windows\` (or `Release\`) and double-click to install in Visual Studio.

### 3. Open the tool window

In Visual Studio: **Tools → Inferpal** (or **Alt+B** / **Alt+O**).

### 4. Configure and connect

1. Open **Inferpal Settings** — pick the **provider** (Ollama / LM Studio / OpenAI-compatible), set the server **URL** (Ollama default: `http://localhost:11434`) and any **API key**, then select a model
2. Optionally configure a **Code Actions model** (for Explain/Fix/Refactor) and an **Inline Completion model** (for ghost-text)
3. Enable **Semantic Indexing** and select an embedding model for `search_codebase`
4. Click **Test** to verify connectivity
5. Switch to the **Inferpal** chat window, type a prompt and press **Enter** or click **↑**

### 5. Add a project context (optional)

Create `.inferpal/context.md` at the root of your solution. Anything written there — coding conventions, architecture decisions, team rules — is automatically injected into every system prompt. Use `/context` in the chat to check what is loaded; use `/clear` to reload after editing.

### 6. Use a remote host (optional)

Inferpal talks to the backend over HTTP, so the inference host doesn't have to be the machine running Visual Studio. Point the server **URL** in Settings at a remote box (e.g. `http://192.168.1.2:11434`) to offload inference to a GPU workstation or home server. Because most backends can't report a GPU's *total* VRAM, set the **VRAM budget** manually for remote hosts (Settings → Context, or `/hardware <gb>`) so the fit-checks and `num_ctx` recommendations work. Full walkthrough: **[Remote Inference Guide](docs/remote-inference.md)**.

---

## Interface

**Keyboard shortcuts:**
- **Enter** — send message
- **Shift+Enter** — insert newline at cursor position
- **Alt+B** / **Alt+O** — open Inferpal chat window from anywhere in VS
- **Alt+M** — run `/map` (call graph) for the active file
- **Ctrl+Shift+I** — Inline Edit (Edit with AI) on the selection
- **Tab** / **Esc** — accept / dismiss a ghost-text completion

### Code actions (editor context menu)

Right-click any selection in the editor → **Inferpal** submenu:

| Action | Description |
|---|---|
| **Edit with AI…** | Inline Edit — rewrite the selection in place from an instruction (**Ctrl+Shift+I**) |
| **Explain** | Explain what the selected code does (answers in the chat) |
| **Fix** | Fix bugs in the selected code — applied in place |
| **Refactor** | Refactor the selected code — applied in place |
| **Add Tests** | Generate unit tests into a separate test file |
| **Add Docstring** | Add an XML documentation comment in place |

> [!NOTE]
> **Edit with AI…**, **Fix**, **Refactor** and **Add Docstring** edit the code directly in
> the editor (undoable with Ctrl+Z). **Add Tests** writes to a separate test file. Only
> **Explain** answers in the chat window.

All code actions use the **Code Actions model** (configurable separately from the chat model) and run without tool calling.

### Slash commands

Type `/` in the prompt field to open the autocomplete popup. Available commands:

| Command | Description |
|---|---|
| `/clear` | Save and clear the conversation |
| `/model <name>` | Switch the active model |
| `/tools on\|off` | Enable or disable tool calling |
| `/export` | Export conversation to `.md` or `.txt` |
| `/restore <path>` | Restore a file from its most recent backup |
| `/undo-run [list]` | Revert every file changed during the last agent run; `list` shows tracked runs |
| `/replay [n]` | Post-mortem timeline of an agent run: tool calls, durations, files touched |
| `/read <path>` | Attach a file as context |
| `/ls <path> [pattern]` | List files in a directory |
| `/grep <dir> <pattern> [ext]` | Search text in files |
| `/run <command>` | Run a shell command (PowerShell on Windows, bash on Linux/macOS, `sh` where there is no bash) |
| `/fetch <url>` | Fetch a web page as text |
| `/search-web <query>` | DuckDuckGo web search (also `/search`, `/web_search`) |
| `/search-code <query>` | Semantic search across the indexed codebase (also `/codebase`) |
| `/git [path]` | Show git status, log, branches, diff summary |
| `/context` | Show the active `.inferpal/context.md` |
| `/xray` | Context X-Ray: interactive breakdown of the system prompt (token bars per layer, per-section toggles, raw-prompt copy); also opens by clicking the context gauge |
| `/build [path]` | Run a build and display errors |
| `/fix-build [path]` | Compile → AI fixes errors → recompile, repeated until clean (max 5 rounds) |
| `/tdd [filter]` | "Fix until green": run tests → AI patches failures → re-run, up to 5 rounds (dotnet / pytest / npm / cargo / go). The failing test is re-run **under the debugger** and its exception/stack/locals feed the fix (one approval per run) |
| `/task [objective]` | Background agent run, detached from the chat — keep working while it investigates; read-only, reports back when done (`/task list\|<id>\|stop <id>\|clear`) |
| `/task propose <objective>` | Background run that may **propose** file changes: every edit is recorded, not applied, and comes back as a reviewable diff. `/task apply <id> <n>` applies one, through the usual approval prompt |
| `/debug <hypothesis>` | Answer a question about runtime behaviour by **observing** it: breakpoint, start (you confirm — it runs your program), read the stack, the locals and the expressions that settle it. `/debug` reports the current state, `/debug stop` ends the session. Both editors |
| `/solution [path]` | Display the solution structure |
| `/map [path]` | Show the dependency graph of a file |
| `/index` | Show the semantic index status (chunks, root, embedding model) |
| `/index rebuild` | Force a full rebuild of the semantic index |
| `/diff` | Attach current `git diff` as a context chip |
| `/commit` | Generate an AI commit message from `git diff` (pre-fills the prompt to confirm) |
| `/commit-exec` | Execute the commit proposed by `/commit` |
| `/memory` | Show `.inferpal/memory.md` (the agent's persistent memory) |
| `/note <text>` | Append a timestamped note to `.inferpal/notes.md` |
| `/notes [clear]` | List or clear all project notes |
| `/history [term]` | List saved sessions, or full-text search across them |
| `/phistory [term]` | Search prompt history; `/phistory use <n>` to reuse |
| `/branch [n\|name]` | Fork the conversation at turn *n*, switch to a branch, or list the branch points and the family tree |
| `/models` | List / pull (streaming, Ollama) / delete / show running models |
| `/hardware [gb]` | Show the GPU/VRAM profile (budget, loaded models, headroom, `num_ctx` advice); `/hardware <gb>` sets the VRAM budget manually (required for remote hosts) |
| `/bench [model…]` | Local test bench: TTFT, tokens/s, VRAM and a 5-task quality micro-eval per model, with per-role recommendations; `/bench last` redisplays the saved run |
| `/arena <prompt>` | Model arena: same prompt to two models, answers blind-labelled A/B; vote with `/arena a\|b\|tie`, standings with `/arena stats` |
| `/setup` | Re-run first-run discovery: auto-detect the backend and auto-select chat + embedding models |
| `/snippets` | `list` / `copy <n>` / `delete <n>` / `clear` saved code snippets |
| `/template [id]` | Load a session template (code-review / bug-hunt / architecture / refactoring / tests) |
| `/prompts [init]` | List reusable prompt files in `.inferpal/prompts/*.md`, or scaffold an example |
| `/docs add <url> [title]` | Crawl & index an external documentation site for the `search_docs` tool |
| `/docs list \| remove <id> \| reindex [id]` | List, remove, or re-crawl indexed documentation sources |
| `/rules [init]` | List project rules in `.inferpal/rules`, or scaffold an example |
| `/checks [init]` | List review checks in `.inferpal/checks`, or scaffold an example |
| `/check [name\|init]` | AI-review the current git diff against the checks (100% local); `<name>` runs one check |
| `/onboard [init\|apply\|context [force]]` | Report the committed project profile (`.inferpal/project.json`) — applied / recommended / refused; scaffold an example, apply the recommendations to this machine, or draft `.inferpal/context.md` from the repository |
| `/plan` | Toggle plan mode — read-only: the agent explores and proposes a plan without editing files |
| `/plan save [name]` | Save the plan from the last answer to `.inferpal/plans/<name>.md` — committed with the code, so it survives `/clear`, a restart and a colleague's clone |
| `/plan list` | List this repository's plans with their progress |
| `/plan <name>` | Open a plan and make it the active one |
| `/plan next` | Show the next unfinished step of the active plan |
| `/plan done <n>` / `/plan undone <n>` | Tick or reopen a step — the file is edited surgically, your prose and notes are untouched |
| `/agent-step` | Toggle agent step mode (pause between tool calls) |
| `/resume` | Resume the agent after a step-mode pause |
| `/diagnostics [list\|clear\|on\|off\|export]` | List swallowed background errors (includes permission-rule denials); `clear` empties, `on`/`off` toggles the file log, `export` copies a sanitized support bundle |
| `/help` | Show all available commands |
| `/explain` | Explain the active code — read-only, answers in the chat |
| `/fix` | Fix bugs in the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
| `/review` | Review the active code — read-only, answers in the chat |
| `/refactor` | Refactor the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
| `/test` | Generate unit tests into a **separate test file** (created/opened, or extended if it exists) |
| `/doc` | Add XML documentation to the active code — **applied directly in the editor** (undoable with Ctrl+Z) |

---

## Available Tools

The model can autonomously call these 28 built-in tools (plus any user-defined shell tools and any tools exposed by connected MCP servers):

| Tool | Description |
|---|---|
| `read_file` | Read the full content of a file |
| `write_file` | Write or overwrite a file (**requires approval**, snapshot saved, Smart Fix runs) |
| `list_files` | List files in a directory (glob pattern, max 300) |
| `search_in_files` | Regex or text search across files (max 100 results) |
| `run_command` | Execute a shell command — PowerShell on Windows, bash on Linux/macOS (`sh` where there is no bash) ; cwd and env overrides persist across calls (**requires approval**, configurable timeout) |
| `apply_diff` | Find-and-replace in a file — exact, then whitespace-tolerant fuzzy fallback; `occurrence` = `unique` / `first` / `all` (**requires approval** showing the diff, snapshot saved, Smart Fix runs) |
| `apply_edits` | Apply many edits across one or more files **atomically** — nothing is written unless every edit resolves (**requires approval**, snapshot per file, Smart Fix runs) |
| `restore_file` | Restore a file from the most recent backup in `.inferpal/history/` |
| `delete_file` | Delete a file (**requires approval**, snapshot saved before deletion) |
| `get_diagnostics` | Run `dotnet build` and return MSBuild errors/warnings (90s timeout) |
| `get_active_document` | Get the path and content of the file currently open in VS |
| `get_open_editors` | List all files currently open in VS, with the active file marked `[active]` |
| `get_git_status` | `git status`, last 20 commits, branches, diff summary; `include_diff=true` for full diff |
| `get_debugger_state` | When paused at a breakpoint/exception: break reason, exception type/message, call stack (`file:line`), and locals of the current frame — read-only; also backs the `@debugger` mention |
| `run_tests` | Run `dotnet test` / `pytest` / `npm test` / `cargo test` / `go test` (auto-detected); returns pass/fail summary and failing test details |
| `fetch_url` | Fetch a web page and return its readable text content (HTML stripped) (**requires approval**, SSRF-guarded) |
| `web_search` | Search the internet via DuckDuckGo — returns title, URL, and snippet per result (**requires approval**) |
| `get_solution_info` | Parse the `.sln` and `.csproj` files — returns projects, frameworks, and packages |
| `insert_at_cursor` | Insert text at the current cursor position in the active VS editor |
| `replace_selection` | Replace the current selection in the active VS editor |
| `update_memory` | Update `.inferpal/memory.md` — the agent's persistent memory for future sessions (append / replace / clear) |
| `analyze_code` | Unified static-analysis facade with `mode=` — `callgraph` (callees/callers of a file), `impact` (blast radius of a change), `nexus` (cross-language REST / JS Interop / SignalR bridges between C# and TS/JS); replaces the former `trace_dependency` / `analyze_impact` / `trace_nexus` tools |
| `search_codebase` | Semantic search across the indexed project — finds relevant code by natural language query |
| `search_docs` | Semantic search across external documentation indexed via `/docs` — returns passages with source URLs |
| `generate_project_map` | Full project map — namespace tree, type hierarchy, dependencies, hotspot files (TTL-cached) |
| `rename_symbol` | Project-wide semantic rename — Roslyn for C#, regex fallback for other languages; `dry_run=true` by default |

### Fix with AI

When `get_diagnostics` returns build errors — whether triggered by the agent or the `/build` slash command — a **Fix with AI** button appears on the tool bubble. Clicking it pre-fills the prompt with:

1. The full list of MSBuild errors
2. The content of every `.cs` file referenced in those errors (up to 5 files × 4 000 chars each)

### Smart Fix Protocol

After every `write_file` / `apply_diff` / `apply_edits` on a build-relevant file, a quick build/typecheck runs automatically. The ecosystem is chosen from the file extension — **.NET** (`dotnet build`), **TypeScript** (`tsc --noEmit`), **Rust** (`cargo check`), **Go** (`go build`) — detected by project marker (`*.csproj`/`*.sln`, `tsconfig.json`, `Cargo.toml`, `go.mod`), and extendable/overridable via `.inferpal/validators.json`. If compilation errors are found, they are appended inline to the tool's response — the agent sees them immediately in the same reasoning loop and can fix without a manual `/build` cycle. A missing toolchain stays silent.

---

## Inline Ghost-Text Completions

As you type in any code file, Inferpal requests Fill-in-the-Middle completions from the model server in the background (supported on Ollama and LM Studio):

- **Tab** — accept the suggestion and insert it at the cursor
- **Esc** (or any other key) — dismiss
- Completions are suppressed when IntelliSense triggers are active (`.`, `(`, `[`, `<`, `"`, `'`, `,`, space)
- Three performance presets control latency and quality:

| Preset | Max tokens | Temperature | Debounce |
|--------|-----------|-------------|---------|
| Fast | 128 | 0.4 | 300 ms |
| Default | 256 | 0.2 | 600 ms |
| High Accuracy | 512 | 0.1 | 1 000 ms |

A dedicated **FIM model** can be configured separately from the chat model (recommended: `qwen2.5-coder`, `deepseek-coder`).

---

## Semantic Codebase Search

Inferpal indexes all source files in your solution in the background using an embedding model from the configured provider. The `search_codebase` tool lets the agent (and you, via `/search-code`) find relevant code by describing what you're looking for in plain language:

```
> search_codebase "authentication token validation"
> search_codebase "database retry with exponential backoff"
> search_codebase "how the compaction algorithm works"
```

Results are ranked by **hybrid search** — semantic cosine similarity *and* lexical BM25, fused with Reciprocal Rank Fusion — and include file paths and line numbers. The lexical side catches exact identifiers and symbol/file names that local embeddings dilute; lexical BM25 alone is used when the embedding model is unavailable. With auto-context on, the most relevant chunks are also injected into each code-related turn automatically.

Supported file types: `.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.py`, `.go`, `.java`, `.cpp`, `.c`, `.h`, `.hpp`, `.rs`, `.fs`, `.razor`, `.vue`

Re-indexing happens automatically on file save (5s debounce). Use `/index rebuild` to force a full reindex.

Background indexing automatically **pauses while a chat/agent request is running** and resumes right after — indexing and chat share the same backend, so the interactive request always gets priority and never starves waiting for the chat model to load.

---

## External Documentation (@Docs)

Index any external documentation site so the agent can answer library/framework questions from the docs themselves — not just from your code:

```
/docs add https://react.dev/learn "React"
/docs list
/docs remove react
/docs reindex
```

`/docs add` crawls the site **same-domain** (following links under the start URL's path, capped at 50 pages / depth 3), converts each page to text, chunks it (~500 tokens), and embeds it in the background — progress is shown as chat bubbles. The agent then retrieves relevant passages with the `search_docs` tool, citing the source page title and URL.

The documentation index is **global** (stored in `%AppData%/Inferpal/docs/docs.db`), so a site you index once is available across every solution. Embeddings reuse the configured RAG embedding model. Falls back to keyword search when embeddings are unavailable.

---

## MCP Servers (Model Context Protocol)

Inferpal can connect to any **stdio or Streamable HTTP [MCP](https://modelcontextprotocol.io) server** and expose its tools to the agent — the same servers used by Claude Desktop and Continue. Enable MCP in Settings and paste a server map (Claude Desktop / Continue format):

```json
{
  "filesystem": {
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\dev"],
    "env": {}
  }
}
```

On startup (or after saving settings) each server is spawned, its tools are discovered via `tools/list`, and they appear to the agent as `mcp__<server>__<tool>`. The client is a home-grown JSON-RPC 2.0 implementation over stdin/stdout with **zero extra NuGet dependencies**. Every MCP tool call is gated by an approval prompt offering **Allow once / Always allow this tool / Cancel** — choosing "always" remembers that tool for the rest of the session (never persisted to disk, since MCP servers run arbitrary external code). Remote servers use **Streamable HTTP**, with static headers (`${ENV}` expansion) or OAuth 2.1 for authentication.

---

## Project Rules & AI Checks

Two repo-versioned, fully-local governance features modelled on Continue's `.continue/rules` and `.continue/checks`.

### Rules — `.inferpal/rules/*.md`

Each markdown file is a rule with optional YAML frontmatter:

```markdown
---
description: C# naming conventions
globs: **/*.cs
alwaysApply: false
---
- Use PascalCase for public members, camelCase for locals.
- Prefix private fields with an underscore.
```

Rules whose glob matches the **active editor file** (or with `alwaysApply: true` / no `globs`) are injected into the system prompt under a `## Rules` section, and the prompt is **re-scoped automatically when you switch files**. `/rules` lists them; `/rules init` scaffolds an example.

### AI Checks — `.inferpal/checks/*.md`

Each markdown file describes review criteria. `/check` grabs the current **git diff** (staged → unstaged) and asks the model to review it against every check, reporting `file:line`, a severity (blocker / warning / nit) and a concrete fix — **100% local**, nothing leaves the machine. `/check <name>` runs a single check; `/checks` lists them; `/checks init` scaffolds an example.

---

## System Prompt Layering

The effective system prompt is built in this order at the start of each conversation:

```
[Base prompt]            ← Strings.SystemPrompt (hardcoded in extension)
[Custom system prompt]   ← Settings → "Custom system prompt" field (optional)
[Pinned files]           ← up to 3 pinned context files (optional)
[## Project context]     ← .inferpal/context.md at solution root (optional)
[## Agent memory]        ← .inferpal/memory.md (optional)
[## Project notes]       ← .inferpal/notes.md (optional)
[## Rules]               ← .inferpal/rules/*.md matching the active file (optional)
```

Use `/clear` after editing the settings or `context.md` to reload the prompt. Rules are re-evaluated automatically when you switch the active editor file.

---

## Agentic Loop

```
User sends a prompt
  ↓
POST chat endpoint  { model, messages, tools: [tool definitions], stream: true }
  (Ollama /api/chat · OpenAI-compatible /v1/chat/completions)
  ↓
Stream response (NDJSON / SSE) → tokens appear in real time (raw text)
  ↓
done=true
  ├─ tool_calls present? → execute tool → append result → repeat (max 20 turns)
  │     write_file / apply_diff / apply_edits → Smart Fix (build/typecheck) → errors inline
  └─ no tool calls → final answer
        ↓
    Parse Markdown → render structured blocks with inline formatting
```

---

## Architecture

The extension is built on the Visual Studio Extensibility model (`Microsoft.VisualStudio.Extensibility.Sdk` 17.14.x), hosted **in-process** since 2026-08-23.

> **Hosting (2026-08-23).** The extension now declares `RequiresInProcessHosting`, so it is
> hosted **inside `devenv.exe`** rather than in a separate host process. This is what makes the
> in-process parts below (ghost text, inline edit, `/tdd` debugger driver) discoverable at all:
> Visual Studio 18 only inventories them through the `MefComponent` / `VsPackage` assets of the
> packaged manifest, and only when that manifest declares the **hybrid** installation type
> `ExtensionType="VSSDK+VisualStudio.Extensibility"` — a registry key does not feed the MEF
> catalog, and with the out-of-process type alone the assets are never processed. The Remote UI data contract
> described below is unchanged: it is a SDK rule, not a consequence of the process split.


### Process boundary

A hard constraint of VS Remote UI, independent of hosting: only types loaded in `devenv.exe` can be referenced in XAML, and all data crossing the view-model boundary must be `[DataContract]` objects containing only primitives. The diagram below names the two sides of that boundary; under in-process hosting both live in `devenv.exe`, and the contract is unchanged.

```
Extension code (view-model side)          Remote UI rendering (devenv.exe)
────────────────────────────────          ──────────────────────────────
IInferenceProvider clients                WPF DataTemplate rendering
  (Ollama / LM Studio / OpenAI)           DataTrigger on MarkdownBlock.Type
ToolRegistry  (28 tools + MCP)              → WrapPanel ItemsControl (paragraph/lists)
MarkdownParser (Markdig 1.2.0)              → TextBlock per InlineRun (bold/italic/code)
  → MarkdownBlock + InlineRun[]             → TextBox RO (code_block)
ProjectIndexService (RAG indexing)          → Border (separator)
InferpalToolWindowData (ViewModel)        Inferpal.InProc (net472, in devenv):
                                            GhostText MEF (adornments → IWpfTextView)
                     ──── Remote UI ────
                          [DataMember]
                          primitives only
```

The `GhostText` components are **in-process**, and they live in their own assembly, `Inferpal.InProc.dll`, targeting **.NET Framework 4.7.2**. That is not a legacy leftover: `devenv.exe` in VS 18 is itself a .NET Framework 4.7.2 process, and its MEF discovery reflects over whatever assembly the manifest declares. Measured on 2026-08-23: across the 211 MEF components of VS 18, the .NET 8 extension assembly was the only one actually linked against `System.Runtime 8.0.0.0`, and every one of its types was rejected with `PartDiscoveryException` — silently, while the out-of-process chat kept working. So the in-process half (MEF parts `IWpfTextViewCreationListener` / `AdornmentLayerDefinition`, plus a minimal `AsyncPackage`) is compiled for the framework `devenv` actually runs, shares its plumbing with `Inferpal.Core` **by source link** rather than by reference, and gets its FIM completions from a small `Inferpal.Fim` sidecar process that hosts the real core over JSON-RPC. Visual Studio discovers the whole thing only through the `Microsoft.VisualStudio.MefComponent` and `Microsoft.VisualStudio.VsPackage` assets declared in `source.extension.vsixmanifest`, and only if that manifest also declares the hybrid installation type `ExtensionType="VSSDK+VisualStudio.Extensibility"` — with the out-of-process type alone the VSIX installs under `Common7\IDE\VSExtensions\` and nothing processes its assets. Miss any of those and the in-process half never loads, and nothing reports it (the chat keeps working).

---

## Adding a Custom Tool

1. Create `Services/Tools/MyTool.cs` implementing `ITool`
2. Use English for `Name`, `Description`, and `Parameters` (best model compatibility)
3. Use `Strings.X(...)` for user-facing return messages (localization)
4. Register it in `ToolRegistry.cs`: `Register(new MyTool())`

```csharp
internal class MyTool : ITool
{
    public string Name        => "my_tool";
    public string Description => "Does something useful.";
    public object Parameters  => new { type = "object", properties = new { }, required = Array.Empty<string>() };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // implementation
        return "result";
    }
}
```

---

## Adding a Language

1. Create `Localization/Strings.XX-YY.resx` with the same keys as `Strings.resx`
2. Add the culture code and display name to `LanguageOptions` in `InferpalSettingsData.cs`
3. Build — the satellite `XX-YY/Inferpal.resources.dll` is generated automatically and included in the VSIX

---

## Contributing

Contributions are welcome! Quick version: implement `ITool`, register it in `ToolRegistry.cs`, add any new strings to all 10 `.resx` files **and** to `Strings.cs`.

---

## Acknowledgments

This project was developed with the assistance of **Claude Opus 4.8** (Anthropic).
