# Features

A functional tour of what Inferpal does. Each area links to a deeper reference where one
exists.

## Agent & tools

- **Agentic loop** — the model autonomously chains tool calls (read/write files, run
  commands, search the web, diagnose builds, run tests, search the codebase) for up to
  **20 turns**, showing each step as a collapsible bubble. Independent read-only tools
  (`read_file`, `list_files`, `search_in_files`) in a single turn run **in parallel**.
- **26 built-in tools** plus user-defined shell tools and [MCP](mcp.md) server tools — see
  **[Tools](tools.md)**.
- **Agent Step Mode** — pause between tool calls to inspect or override each action. Toggle
  with the 🦶 button (or `/agent-step`); continue with **▶ Resume** (or `/resume`).
- **Agent Orchestrator** — an optional Plan→Act→Observe loop (`agentModeEnabled`).
- **Plan mode** (`/plan`) — read-only: the agent explores and proposes a plan without editing
  any files.
- **Multi-file approval pass** — after ≥2 file writes in one run, a **Restore All** button
  rolls everything back at once.
- **Undo a whole run** — `/undo-run` reverts every file changed during the last agent run
  (restores edited files, deletes files created that run); `/undo-run list` shows the
  session's tracked runs.

## Code editing & fixing

- **Code actions** (editor context menu → **Inferpal**) — **Explain**, **Fix**, **Refactor**,
  **Add Tests**, **Add Docstring**. They use a dedicated model, run without tool calling, and
  answer in the chat window.
- **Inline Edit (Edit with AI)** — select code (or place the caret on a line), press
  **Ctrl+Shift+I** (or pick **Edit with AI…** from the context menu), type an instruction, and
  the model rewrites the selection **directly in the editor** (re-indented to match the
  original). Uses the Inline Edit model (falls back to Code Actions → chat model).
- **Atomic multi-file edits** — `apply_diff` matches `old_content` exactly, then falls back to
  a **whitespace-tolerant** match (indentation / trailing spaces / line endings) and supports
  `occurrence` = `unique` / `first` / `all`. `apply_edits` applies many edits across several
  files **all-or-nothing**: nothing is written unless every edit resolves.
- **Smart Fix Protocol (polyglot)** — after every `write_file` / `apply_diff` / `apply_edits`
  on a build-relevant file, a quick build/typecheck runs automatically and any compilation
  errors are fed back inline so the agent fixes them in the same loop. The ecosystem is picked
  from the file extension — **.NET / TypeScript / Rust / Go** built in, extendable via
  `.inferpal/validators.json`.
- **Fix with AI** — when a build fails (agent, `/build`, or a VS solution build), a button
  pre-fills the prompt with the MSBuild errors **and the content of each affected file** (up
  to 5 files × 4 000 chars).
- **Build Failed banner** — when Visual Studio finishes a solution build with errors, a banner
  appears above the input with the first error and a one-click **Fix with AI** / `/fix-build`
  entry point.
- **Inline diff viewer** — an LCS-based diff is shown in the chat bubble after every
  write/apply (added green, removed red, unchanged collapsed).
- **`/fix-build`** — compile → AI fixes errors → recompile, repeated until clean (max 5 rounds).

## Inline completions

- **Ghost-text Fill-in-the-Middle** as you type — **Tab** to accept, **Esc** to dismiss.
- Three presets: **Fast** (128 tok / 0.4 / 300 ms) · **Default** (256 / 0.2 / 600 ms) ·
  **High Accuracy** (512 / 0.1 / 1 000 ms).
- Suppressed while IntelliSense triggers are active. Supported on Ollama and LM Studio.

## Search & knowledge

- **Semantic codebase search** — background indexing with 3-tier chunking (Roslyn → LSP →
  regex) and **hybrid retrieval** (cosine + BM25 lexical fused with RRF, so exact identifiers
  rank well). Shadow pre-warm, Smart Auto-attach chips, and **per-turn auto-context** that
  silently injects the most relevant chunks into each code question. See
  **[Search & Indexing](search-and-indexing.md)**.
- **@Docs** — crawl and index external documentation sites, queried via `search_docs`.
- **MCP client** — expose tools from any stdio or Streamable HTTP MCP server. See **[MCP](mcp.md)**.
- **`@`-mentions** — a typed context picker (`@file`, `@diff`, `@debugger`, …). See
  **[Mentions](mentions.md)**.

## Context & memory

- **System prompt layering** — base prompt + custom prompt + pinned files + project context
  + agent memory + project notes + matching rules. See
  **[Architecture](architecture.md#system-prompt-layering)**.
- **Persistent project context** — `.inferpal/context.md`, injected into every prompt.
- **Agent memory** — `.inferpal/memory.md`, updated by the `update_memory` tool / `/memory`.
- **Project notes** — `/note` appends timestamped notes to `.inferpal/notes.md`.
- **Pinned context files** — up to 3 files always injected (📌 toolbar button or a promoted
  chip).
- **Context compaction** — old messages are summarized by the LLM instead of being
  hard-truncated, triggered at ~80 % of the context budget; **KV-cache anchor** preserves
  the first N messages verbatim.
- **Workspace auto-context** — the first message of every session silently attaches solution
  info + open editors.
- **Project rules & AI checks** — repo-versioned governance. See
  **[Rules & Checks](rules-and-checks.md)**.

## Conversation experience

- **Real-time streaming** — tokens appear as generated; Markdown renders once complete.
- **Real-time context & token gauge** — the header token counter and context-fill bar update
  live during generation (provisional `~` values), then snap to the exact
  `prompt_eval_count + eval_count`. Fill colour ramps green → amber → orange → red at
  50 / 80 / 95 %.
- **Markdown rendering** — headings (H1–H3), selectable code blocks with a copy button,
  lists, **bold**, *italic*, `inline code`; `<think>` tags are stripped automatically.
- **Regenerate** the last assistant reply in one click.
- **Conversation search** — 🔍 header button; non-matching messages dim to 20 % opacity.
- **Session persistence & export** — sessions auto-save with a 4–5 word AI-generated title;
  export to `.md` / `.txt` with a stats header (model, turns, tool calls, tokens, duration).
- **Sound notification** — an audible ping when a run finishes after more than 30 seconds.
- **Code snippet library** — ⭐ saves any code block; `/snippets` manages it across sessions.
- **Session & prompt templates** — `/template` loads a preconfigured context; user prompt
  templates support `{args}`.
- **Prompt history** — persistent; search with `/phistory`.
- **Welcome screen** — an empty session shows one-click suggestion cards (Explain the
  selection, Fix an error, Generate a test, See all commands) plus the active model and mode.
- **Attach file / selection** — 📎 toolbar buttons attach a file or the current editor
  selection as a context chip (the same context you can add with `/read`, `@file`, or `@code`).

## Models & hardware

- **Model manager** — `/models list/pull/delete/running` (streaming pull on Ollama).
- **VRAM monitoring** — a header badge shows the models resident in VRAM;
  `ModelLifetimeService` auto-unloads idle ones.
- **Hardware profile** — `/hardware` reports budget, loaded models, headroom, and a
  recommended `num_ctx`.
- **Dynamic timeout engine** — Quick / Normal / Deep thresholds per task complexity.
- **Heartbeat & connection guard** — a silent pre-flight before every send; the Send button
  greys out when the server is unreachable, and recovers automatically.

## Safety

- **Workspace-confined file operations** — a single `AssertUnderRoot` sandbox on every
  path-taking tool.
- **Approval prompts** — `write_file`, `apply_diff`, `apply_edits`, `delete_file`,
  `run_command`, `rename_symbol`, `fetch_url`, `web_search`, custom shell tools, and MCP calls
  each prompt **Allow once / Always allow this tool / Cancel** (session-scoped, never
  persisted). Edit prompts show the **actual diff** before you confirm.
- **Permission rules** — `allow` / `deny` patterns (per-machine + committable
  `.inferpal/permissions.json`) classify a call before the prompt; a built-in,
  **non-bypassable denylist** of catastrophic shell commands always applies. See
  **[Tools → Permission rules](tools.md)**.
- **Hardened SSRF guard** on outbound fetches (DNS rebinding, IPv4-mapped IPv6, `0.0.0.0/8`,
  loopback/private ranges, ReDoS-safe timeout).
- **Circuit breaker** on backend failures and **loop detection** to stop infinite agent loops.

## Localization & theming

- **10 UI languages**, following Visual Studio or overridden independently.
- **VS theme awareness** — colors adapt to Light / Dark / Blue automatically.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| **Alt+B** / **Alt+O** | Open the Inferpal chat window from anywhere |
| **Alt+M** | Run `/map` (call graph) for the active file |
| **Ctrl+Shift+I** | Inline Edit (Edit with AI) on the selection |
| **Enter** | Send the message |
| **Shift+Enter** | Insert a newline in the prompt |
| **Tab** / **Esc** | Accept / dismiss a ghost-text completion |
