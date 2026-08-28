# Features

A functional tour of what Inferpal does. Each area links to a deeper reference where one
exists.

## Two editors, one engine

- The **Visual Studio extension** (primary) and the **VS Code extension** run the same
  `Inferpal.Core` engine and share the same configuration. Since 1.2.0 the VS Code front-end
  is at **feature parity**: full markdown rendering, welcome screen, connection + VRAM badge,
  collapsible tool bubbles (input/output, "Fix with AI" on errors), live agent-plan block,
  slash-command autocomplete, Shift+↑/↓ prompt history, token counter with a clickable context
  gauge (opens the X-Ray panel), conversation search, `.md`/`.txt` export, Ctrl+Alt+I
  keybinding, and a localized webview (10 languages).
- **Headless slash commands** — ~40 commands are served by the host over JSON-RPC with the
  same approval/permission pipeline; long commands are cancellable. VS-only: `/fix-build` and
  `/setup`, coupled to Visual Studio by construction (MSBuild banner, live model-pull bubble).
- **Settings panel in VS Code** — an "Inferpal Settings" webview mirroring the four VS tabs
  (Connection / Behavior / Context / Tools), backed by the shared config.
- **Typed @-mentions, `/plan` and `/agent-step` modes, and per-turn RAG auto-context** all
  work in VS Code too.

## Agent & tools

- **Agentic loop** — the model autonomously chains tool calls (read/write files, run
  commands, search the web, diagnose builds, run tests, search the codebase) for up to
  **20 turns**, showing each step as a collapsible bubble. Independent read-only tools
  (`read_file`, `list_files`, `search_in_files`) in a single turn run **in parallel**.
- **28 built-in tools** plus user-defined shell tools and [MCP](mcp.md) server tools — see
  **[Tools](tools.md)**.
- **Agent Step Mode** — pause between tool calls to inspect or override each action. Toggle
  with the 🦶 button (or `/agent-step`); continue with **▶ Resume** (or `/resume`).
- **Agent Orchestrator** — an optional Plan→Act→Observe loop (`agentModeEnabled`).
- **Plan mode** (`/plan`) — read-only: the agent explores and proposes a plan without editing
  any files.
- **Persistent plans** (`/plan save`) — a plan proposed in the chat used to die at the next
  `/clear`. Saved, it becomes `.inferpal/plans/<name>.md`: numbered steps with checkboxes,
  committed with the code, so it survives a restart, shows up in the review of the change it
  describes, and reaches a colleague. `/plan next` picks it back up, `/plan done <n>` ticks a
  step. Ticking edits **one character** of the file — the prose and notes you wrote by hand are
  preserved byte for byte, because a plan is a document you own and the product only annotates.
  Nothing is ever executed from a plan file: it arrives with any clone, so its text steers the
  model exactly as a rules file does and grants nothing, and every action it leads to goes
  through the usual approval prompt. There is deliberately no "run the whole plan".
- **Multi-file approval pass** — after ≥2 file writes in one run, a **Restore All** button
  rolls everything back at once.
- **Undo a whole run** — `/undo-run` reverts every file changed during the last agent run
  (restores edited files, deletes files created that run); `/undo-run list` shows the
  session's tracked runs.
- **Run replay** — `/replay [n]` shows a post-mortem timeline of an agent run: every tool
  call with its target and duration, then the files it touched.

## Code editing & fixing

- **Code actions** (editor context menu → **Inferpal**) — **Explain**, **Fix**, **Refactor**,
  **Add Tests**, **Add Docstring**. All use a dedicated model and run without tool calling.
  **Fix**, **Refactor** and **Add Docstring** apply their result **directly in the editor**
  (undoable with Ctrl+Z); **Add Tests** writes into a **separate test file** (created/opened,
  or extended if it already exists); only **Explain** answers in the chat window.
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
- **Inline diff preview for code actions** — `/fix`, `/refactor` and `/doc` no longer rewrite
  the buffer blind: the change is shown in the editor with per-hunk accept/reject (Visual
  Studio: a red/green adornment with ✓/✗ per hunk; VS Code: the native Refactor Preview).
  Accepted hunks apply as a single undo step. Toggle with `inlineDiffPreviewEnabled`
  (default on).
- **Inline diff viewer** — an LCS-based diff is shown in the chat bubble after every
  write/apply (added green, removed red, unchanged collapsed).
- **`/fix-build`** — compile → AI fixes errors → recompile, repeated until clean (max 5 rounds).
- **`/tdd [filter]`** — "fix until green", the test-side twin of `/fix-build`: runs the test
  suite (`run_tests` auto-detects dotnet / pytest / npm / cargo / go), hands the failure
  report to the agent, re-runs, up to 5 rounds. Writes go through the usual approval
  pipeline and `/undo-run` applies.
  On the first red round the failing test is also **re-run under the debugger**, and what the
  runner's text cannot carry — the real exception, the call stack, the values of the locals at
  the point of failure — is added to the fix prompt. Visual Studio attaches its own debugger,
  VS Code drives the Debug Adapter Protocol; running a test under a debugger is execution, so
  it asks **once per `/tdd` run**. No debugger, a failed capture or a refused approval falls
  back to the text-only loop, and a failed capture says so in the run. Rewriting a *test* file
  during the loop always asks, whatever your permission rules say — "make it green" has an
  obvious shortcut, and this is the one place it must not be taken silently.
- **`/task <objective>`** — a background agent run, detached from the conversation: submitting
  returns at once and you keep working while it investigates. Tasks run one at a time and wait
  for the chat to be idle before taking the GPU, so interactive work is never blocked. A notice
  appears when the report is ready (`/task <id>` reads it, `/task` lists, `/task stop <id>`
  cancels). Background runs are **read-only** — they explore and report, never write, execute or
  reach the network, so nothing can interrupt you with an approval prompt.
- **`/task propose <objective>`** — a background run that can *describe* writes without making
  them: each write is recorded at the exact point it would have asked for approval, and never
  granted. `/task apply <id> <n>` replays one proposal through the ordinary approval prompt,
  real diff included; a proposal whose file changed since is refused instead of applied.
  Consent still happens at apply time, one write at a time — never in advance.
- **`/debug [hypothesis]`** — settle a question about runtime behaviour by observing it: the
  agent starts a **real debug session** (after asking you first), sets breakpoints, steps, and
  reads locals and the call stack through two read-only tools (`debug_control`,
  `debug_inspect`). Before launching, the target is built through the automation, so a solution
  that does not compile is refused instead of freezing the IDE on a modal. Visual Studio drives
  its own debugger in-process; VS Code goes through a DAP bridge — same command, either editor.
  `/debug` alone reports the current break state; `/debug stop` ends the session.

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
- **Context X-Ray** — `/xray` (or a click on the context gauge) opens an interactive panel
  breaking down everything composing the system prompt: token bars per layer (base, custom,
  pinned, project files, scoped rules), the exact content of each section, per-section on/off
  toggles for the next turn, copy of the raw prompt, and an overhead warning when the fixed
  layers dominate the budget.
- **Project rules & AI checks** — repo-versioned governance. See
  **[Rules & Checks](rules-and-checks.md)**.
- **Project profile & `/onboard`** — `.inferpal/project.json` travels with the repository and
  says how it likes to be worked on. Index exclusions apply on their own (additively — a clone
  can exclude more, never index more); model roles and context size are recommended, shown next
  to the values in effect, and written to your settings only by `/onboard apply`; everything else
  is ignored, unknown keys included. `/onboard context` drafts `.inferpal/context.md` from the
  repository (layout, README, recent commits) and opens it for you to correct. See
  **[Configuration → The project profile](configuration.md#the-project-profile-inferpalprojectjson)**.

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
- **Conversation branching** — `/branch <n>` forks the conversation at turn *n*: the branch keeps
  turns 1..*n* and the conversation continues in it, while the original is written back to disk
  first — exactly as it stands, under a generated name if it never had a file — so branching can
  never lose the half left behind.
  `/branch` lists the branch points and the family tree, `/branch <name>` switches branch.
  A branch is a plain session file with a `parent` + `fork_turn` link — nothing else in the
  store, the history rebuild or the picker had to learn about branching.
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
- **Model bench** — `/bench [model…]` runs a local test bench of installed models: warm
  time-to-first-token, tokens/s, VRAM pressure and a 5-task quality micro-eval scored by
  programmatic assertions (no LLM judge), with per-role recommendations (agent / utility /
  FIM) feeding the Model Router. `/bench last` redisplays the persisted run.
- **Model arena** — `/arena <prompt>` sends the same prompt to two models (sequentially — one
  GPU) and shows both answers blind-labelled A/B; `/arena a|b|tie` records the vote and
  reveals the models, `/arena stats` shows the cumulative local standings.
- **Model Router** — a dedicated **utility model** role (`utilityModel`) handles session
  titles, `/commit` message proposals and compaction summaries instead of the chat model.
  Opt-in **auto mode** (`modelRouterAuto`): with no utility model set, background tasks use
  the model `/bench` recommended for the utility role — but only when it is already warm in
  VRAM; a cold model is never loaded for a title or commit message.
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
- **Permission rules** — `allow` / `deny` patterns classify a call before the prompt. The
  per-machine setting can do both; the committable `.inferpal/permissions.json` overlay can only
  *deny* (a cloned repository must not be able to grant itself auto-approval); a built-in denylist of
  catastrophic shell commands always applies (an **accident guard**, not a security boundary —
  it matches text, so obfuscation defeats it), and indirect execution — PowerShell (`iex`,
  `-EncodedCommand`, `FromBase64String`, `[scriptblock]::Create`, `& $var`) and POSIX
  (`eval`, piping into a shell, `base64 -d`, `sh -c "$var"`, `source`/`exec` on a variable)
  alike — is **force-prompted**: never auto-approved by any rule, session grant or setting,
  never blocked — the approval prompt, where the raw command is visible, is the actual boundary.
  See **[Tools → Permission rules](tools.md)**.
- **Hardened SSRF guard** on outbound fetches (DNS rebinding, IPv4-mapped IPv6, `0.0.0.0/8`,
  loopback/private ranges, ReDoS-safe timeout).
- **Circuit breaker** on backend failures and **loop detection** to stop infinite agent loops.
- **Local diagnostics, transparent export** — `/diagnostics` lists background errors swallowed
  best-effort (in-memory ring buffer, zero telemetry), and `/diagnostics export` copies a
  **sanitized support bundle** for GitHub issues: remote endpoints redacted, API key never
  included, profile and workspace paths masked. The chat shows exactly the text that was
  copied — you read what you are about to send, and nothing leaves the machine on its own.

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
