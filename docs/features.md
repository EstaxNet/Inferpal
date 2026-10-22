# Features

A functional tour of what Inferpal does. Each area links to a deeper reference where one
exists.

## Two editors, one engine

- The **Visual Studio extension** (primary) and the **VS Code extension** run the same
  `Inferpal.Core` engine and share the same configuration: the agent, its tools and approvals, search, MCP, ghost
  text, `/tdd` and `/debug` are the same in both. The VS Code chat has full markdown rendering, welcome screen,
  connection + VRAM badge, collapsible tool bubbles (input/output, "Fix with AI" on errors), live agent-plan block,
  slash-command autocomplete, Shift+↑/↓ prompt history, token counter with a clickable context gauge (opens the X-Ray
  panel), conversation search, `.md`/`.txt` export, Ctrl+Alt+I keybinding, and a localized webview (10 languages).
- **The editor's gestures, in both** — **Edit with AI** (an instruction on the selection or the caret's line, the
  change shown before it applies), **Explain**, **Add Tests**, **Fix**, **Refactor** and **Document** in the editor's
  menu, `/test` on the **selection** with its tests written by an edit you can undo, and `/map` on **Alt+M**.
- **Not in VS Code yet** — a few chat features are Visual Studio-only for now:
  - the **colored diff in a tool bubble** after a write (VS Code shows it on the approval card, before the write — so
    not once the tool is always allowed);
  - the **Build Failed banner** (VS Code's welcome card offers **Fix them** for the Problems panel's errors);
  - the **auto-attach chips** while you type (the per-turn auto-context is there);
  - the **sound** at the end of a long run;
  - `/fix-build` (it answers that it is not available).
- **First run, in both editors** — Inferpal looks for a model server where one usually listens (Ollama, LM Studio,
  llama-server, vLLM) and says what each place answered. VS Code shows it on a card in the chat: the models proposed
  for your card, a server on another machine by its address, a key when a server asks for one, and — on Ollama
  without a chat model — a download of a recommended model. Visual Studio takes the server that answers when nothing
  was set up yet, and says so; `/setup` looks again in both.
- **Not in Visual Studio yet** — **Review a Commit…** (VS Code's command palette lists the commits to pick from; in
  Visual Studio, type `/check <commit>`).
- **Headless slash commands** — nearly all the slash commands are served by the host over JSON-RPC with the
  same approval/permission pipeline; long commands are cancellable.
- **Settings panel in VS Code** — an "Inferpal Settings" webview with the same seven pages as the
  Visual Studio window, read from the same description in the Core, backed by the shared config.
  `/settings` opens it from the chat (and the settings window in Visual Studio).
- **Zed, JetBrains IDEs, Neovim, Emacs** — the same host, run with `--acp`, is an
  [Agent Client Protocol](acp.md) agent: modes and model as thread options, the real diff before every change,
  sessions saved and reloaded. Its settings are the same seven pages, in the browser: `/settings` in a thread, a button
  at first setup, or `Inferpal.Host --settings`; saved, they apply to the open threads from their next question.
- **Typed @-mentions, `/plan` and `/agent-step` modes, and per-turn RAG auto-context** all
  work in VS Code too.

## Agent & tools

- **Agentic loop** — the model autonomously chains tool calls (read/write files, run
  commands, search the web, diagnose builds, run tests, search the codebase) for up to
  **20 turns**, showing each step as a collapsible bubble. Independent read-only tools
  (`read_file`, `list_files`, `search_in_files`) in a single turn run **in parallel**.
- **30 built-in tools** plus user-defined shell tools and [MCP](mcp.md) server tools — see
  **[Tools](tools.md)**.
- **Steer a running turn** — while the agent works, type a correction and press **Enter**: it is
  a note the run reads before its next step ("use `ILogger`, not `Console`", "that is the wrong
  file"), shown in the turn as waiting, then as read. Nothing is stopped and nothing is explained
  again. **Ctrl+Enter** sends the message after the turn instead, in the queue above the input box
  (each entry can be removed). A turn with no step to read a note before — a chat without tools,
  a code action, a slash command — queues it. A note the run could not read (typed during the
  final answer) is sent next; after **Stop**, notes and queued messages come back to the input
  box. With an approval card waiting, Enter with text refuses the card and the text goes with the
  refusal. The conversation keeps the notes with their question: the next question, a reload and
  **Regenerate** see them. Stop is the button — Enter never stops a turn.
- **Agent Step Mode** — pause between tool calls to inspect or override each action. Toggle
  with the 🦶 button (or `/agent-step`); continue with **▶ Resume** (or `/resume`). A note typed
  during the pause resumes the run, which reads it at the next step.
- **Agent Orchestrator** — an optional Plan→Act→Observe loop: the **Agent** mode of the Chat/Agent
  switch (`agentModeEnabled`, `inferpal.agentMode` in VS Code). **Chat** mode still uses tools — the
  simple tool loop, without a plan — and `/tools off` gives a chat without tools. Both editors start in
  Chat mode.
- **Turning tools off** — `/tools` lists every tool with what it weighs in each request; `/tools off <tool>` (or the
  Tools page's button) turns one off in every conversation, built-in or MCP: the model is never offered it, and your
  own commands that use it keep working. See [Tools](tools.md#turning-a-tool-off).
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
- **A run you can read at a glance** *(both editors)* — a run's steps show as they come, then
  fold into one line (`3 steps · read 1 file · edited 1 · build passed`, a ✗ when its last check
  failed). Under the answer, a **result bar** lists the files it changed with their lines added and
  removed, the last build or test check, and **Undo run** (`/undo-run`), offered on the latest run.
- **Approvals in the conversation** *(both editors)* — during a turn, an approval is a card under
  the steps: what will happen, to what, the first lines of the change, then **Allow once**
  (Enter), **Always this session** and **Deny** (Esc); *Open diff* shows the whole change. When a
  narrow rule describes the call, a fourth button offers it — *Always: commands that start with
  `dotnet test`*, *Always: writes under `tests/`* — and writes it to the per-machine permission
  rules, said in the conversation ([Tools → Approval model](tools.md#approval-model)). Enter
  and Esc answer only from an empty message box: the card never takes the keyboard, so a key typed
  for something else cannot approve. Outside a turn (a code action, a slash command), the dialog
  asks as before. A card left unanswered for 15 seconds while the editor does not have the focus
  says so outside the panel: Visual Studio flashes its taskbar button, VS Code shows a notification
  that closes with the answer.
- **Long runs stay awake** *(both editors, and ACP)* — while the model works (an answer, an agent
  run, a `/task`), the machine does not go to sleep: a power request on Windows, `caffeinate` on
  macOS, `systemd-inhibit` on Linux, released ten seconds after the last request and when the
  editor closes, however it closes. A turn waiting for your approval lets the machine sleep:
  nothing is in flight meanwhile. Where the system refuses (no systemd, WSL), `/diagnostics` says
  so once.
- **Continue** *(both editors)* — a turn stopped at its iteration limit, or an answer stopped at
  the length limit, ends with a notice and a **Continue** button: it sends "Continue." as your
  next question. It is not offered after a loop or an answer that repeated itself, where
  continuing would do the same again.
- **Undo a whole run** — `/undo-run` reverts every file changed during the last agent run
  (restores edited files, deletes files created that run); `/undo-run list` shows the
  session's tracked runs. **What commands changed is undone too**: in a git repository, the
  files a `run_command`, a test run, one of your shell tools or an MCP tool changed are recorded
  like the agent's own edits — byte for byte, nothing written to your repository — and counted on
  the result bar. A file you save while an approval card waits is yours, not the command's, and so
  is a file your editor held unsaved changes for when the command started. What could not be
  recorded is said under the undo: a command run outside a git repository, a background job after
  it started, a file larger than 20 MB or stored with Git LFS. Files git ignores (build output,
  `node_modules`, an ignored `.env`), nested repositories and submodules are never recorded, and an
  undo gives back files, never a commit, a branch or the index.
- **Where the backups live** — `/undo-run`, `/restore` and `restore_file` read the copy Inferpal
  keeps of a file before each change: in `.inferpal/history/` at the repository root (ignored by
  git from inside the folder — Inferpal never edits your `.gitignore`), or, with *Backups of
  changed files* set to **In my user folder** (`historyStorage: user`), in Inferpal's local
  application data, one folder per repository, with nothing new written into the project. Switching
  keeps the backups already made restorable.
- **Run replay** — `/replay [n]` shows a post-mortem timeline of an agent run: every tool
  call with its target and duration, then the files it touched.

## Code editing & fixing

- **Code actions** (editor context menu → **Inferpal**) — **Explain**, **Fix**, **Refactor**,
  **Add Tests**, **Add Docstring** (Visual Studio; VS Code's editor context menu has *Inferpal: Fix This Code*,
  *Refactor This Code* and *Document This Code*, and the others are the `/explain` and `/test` commands). All use a
  dedicated model and run without tool calling.
  **Fix**, **Refactor** and **Add Docstring** apply their result **directly in the editor**
  (undoable with Ctrl+Z); **Add Tests** writes into a **separate test file** (created/opened,
  or extended if it already exists); only **Explain** answers in the chat window.
- **Inline Edit (Edit with AI)** — select code (or place the caret on a line), press
  **Ctrl+Shift+I** (or pick **Edit with AI…** from the context menu), type an instruction, and
  the model rewrites the selection **directly in the editor** (re-indented to match the
  original), shown first when *Show the change before applying it* is on — in VS Code, in its Refactor Preview. Uses
  the Inline Edit model (falls back to Code Actions → chat model); the same prompt in both editors.
- **Atomic multi-file edits** — `apply_diff` matches `old_content` exactly, then falls back to
  a **whitespace-tolerant** match (indentation / trailing spaces / line endings) and supports
  `occurrence` = `unique` / `first` / `all`. `apply_edits` applies many edits across several
  files **all-or-nothing**: nothing is written unless every edit resolves.
- **Smart Fix Protocol (polyglot)** — after every `write_file` / `apply_diff` / `apply_edits`
  on a build-relevant file, a quick build/typecheck runs automatically and any compilation
  errors are fed back inline so the agent fixes them in the same loop. The ecosystem is picked
  from the file extension — **.NET / TypeScript / Rust / Go** built in, extendable via
  `.inferpal/validators.json`. When the toolchain is not installed, Smart Fix stays silent — told
  apart from a failed build by the shell's own signal, whatever the system's language.
- **Fix with AI** — when a build fails (agent, `/build`, or a VS solution build), a button
  pre-fills the prompt with the MSBuild errors **and the content of each affected file** (up
  to 5 files × 4 000 chars).
- **Build Failed banner** — when Visual Studio finishes a solution build with errors, a banner
  appears above the input with the first error and a one-click **Fix with AI** / `/fix-build`
  entry point.
- **Inline diff preview for code actions** — `/fix`, `/refactor` and `/doc` no longer rewrite
  the buffer blind: the change is shown in the editor with per-hunk accept/reject (Visual
  Studio: a red/green adornment with ✓/✗ per hunk, and ✓✓/✗✗ on the first open hunk to decide
  all the hunks not yet answered; VS Code: the native Refactor Preview).
  Accepted hunks apply as a single undo step. Toggle with `inlineDiffPreviewEnabled`
  (default on).
- **Inline diff viewer** (Visual Studio) — an LCS-based diff is shown in the chat bubble after every
  write/apply (added green, removed red, unchanged collapsed). VS Code shows the colored diff on the approval card,
  before the write.
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
  real diff included, once the task has ended; a proposal whose file changed since is refused instead of applied.
  A task stopped with `/task stop` keeps what it had proposed.
  There is one proposal per file: edits to different parts of a file are combined into it, and
  when two edits touch the same lines the later one replaces the earlier — the task is told so.
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
- Three speeds, picked as cards in the settings: **Fast** (128 tok / 0.4 / 300 ms) · **Balanced** (256 / 0.2 /
  600 ms) · **Accurate** (512 / 0.1 / 1 000 ms).
- Suppressed while IntelliSense triggers are active. Supported on Ollama, LM Studio, and OpenAI-compatible servers that
  complete around the cursor (llama-server, Ollama's `/v1`) — see [Providers](providers.md).
- **On a server of their own** — the chat on a team server, ghost text on this machine: *Runs on › Another server* on
  the *Server and models* page (its kind and key under *Connection*, with the advanced settings), or
  `inlineCompletionRunsOn` in `config.json`. That server gets its own key, never the chat's, and its own model — the
  chat's is not there to fall back on. Ghost text on another machine than the chat's no longer waits for a chat answer
  to finish; on the chat's machine — under any of its names or addresses — it still does, the card being one.

## Search & knowledge

- **Semantic codebase search** — background indexing with 3-tier chunking (Roslyn → LSP →
  regex) and **hybrid retrieval** (cosine + BM25 lexical fused with RRF, so exact identifiers
  rank well). Shadow pre-warm and Smart Auto-attach chips (Visual Studio), and **per-turn auto-context** that
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
- **Pinned context files** — up to 3 files always injected (the composer's attach menu, or a
  promoted chip).
- **Context compaction** — old messages are summarized by the LLM instead of being
  hard-truncated, triggered at ~80 % of the context budget; **KV-cache anchor** preserves
  the first N messages verbatim. The summary request is sized to the window of the model
  that writes it: when the oldest messages don't fit, the summary covers the most recent
  ones and says how many it misses.
- **Workspace auto-context** — the first message of every session silently attaches solution
  info + open editors.
- **Context X-Ray** — `/xray` (or a click on the context gauge) opens an interactive panel
  breaking down everything composing the system prompt: token bars per layer (base, custom,
  pinned, project files, scoped rules), the exact content of each section, per-section on/off
  toggles for the next turn, copy of the raw prompt, and an overhead warning when the fixed
  layers dominate the budget.
- **Project rules & AI checks** — repo-versioned governance. See
  **[Rules & Checks](rules-and-checks.md)**.
- **Instructions your repository already gives coding agents** — `AGENTS.md` (from the git root
  down to the active file's folder), `.github/copilot-instructions.md` and
  `.github/instructions/*.instructions.md` (`applyTo`), `CLAUDE.md` with the files it imports,
  `.claude/rules/`, `.cursor/rules/*.mdc`, `.cursorrules`, `.clinerules`, `.roo/rules/` and
  `.continue/rules/` are read as each tool reads them and sent — those of the repository's root in
  the system prompt, after pinned files and before `.inferpal/`, which wins a conflict; those of the
  active file's folders and globs with the question, in a block sent when the active file changes,
  so the start of the request never moves with it. A file identical to another is sent once; the
  root's share the prompt's file budget, the active file's have their own, and a cut says so. Rules meant to load on demand or by hand are not sent yet.
  `/instructions` and the Context settings page list every file found — its tool, when it
  applies, what the next question sends of it (cut or whole), and why not when it does not —
  and `repoInstructionFamilies` chooses which tools' files are read (all by default).
- **Commands your repository already wrote** — `.github/prompts/*.prompt.md` (Copilot),
  `.claude/commands/**/*.md` (Claude Code, subfolders as `/folder:name`) and `.continue/prompts/` (Continue) are
  slash commands too, named as their tool names them, with their origin in the autocomplete and `/help`.
  `${input:…}`, `$ARGUMENTS` and `{{{ input }}}` receive the words typed after the command, and a command
  that asks for none gets them at the end. A command named like a built-in, or like an earlier one, is
  dropped and `/prompts` says by what; a Claude Code `` !`command` `` line is never run before sending — the
  model is asked to run it, under approval.
- **Skills** — the `SKILL.md` folders a repository wrote for Copilot (`.github/skills`), Claude Code
  (`.claude/skills`), any agent of the Agent Skills standard (`.agents/skills`), Cline or Roo, and your own
  (`~/.copilot/skills`, `~/.claude/skills`, `~/.agents/skills`). `/skill` lists them; `/skill <name> <request>`
  joins the skill's instructions and the list of its files to your question, named under it like an attached file.
  The model reads the skill's other files with `read_skill_file`, confined to that skill's folder, and runs a script
  only through `run_command` — with your approval: `allowed-tools` grants nothing. A repository's skill wins over
  yours of the same name, and `/skill` names the one left out. Writing into a skill folder always asks.
  With `skillsAutoMode` on (off by default), every question lists the skills and the model loads the one a request
  needs by itself.
- **Project profile & `/onboard`** — `.inferpal/project.json` travels with the repository and
  says how it likes to be worked on. Index exclusions apply on their own (additively — a clone
  can exclude more, never index more); model roles and context size are recommended, shown next
  to the values in effect, and written to your settings only by `/onboard apply`; everything else
  is ignored, unknown keys included. `/onboard context` drafts `.inferpal/context.md` from the
  repository (layout, README, recent commits) and opens it for you to correct. See
  **[Configuration → The project profile](configuration.md#the-project-profile-inferpalprojectjson)**.

## Conversation experience

- **Real-time streaming** — tokens appear as generated; Markdown renders once complete.
- **Real-time context ring** — the composer's ring (share of the window; token counts in its
  tooltip) updates live during generation (provisional `~` values), then snaps to the exact
  `prompt_eval_count + eval_count`. Its colour ramps amber → orange → red at 50 / 80 / 95 %; a
  click opens the Context X-Ray.
- **Markdown rendering** — headings (H1–H3), selectable code blocks with a copy button,
  lists, **bold**, *italic*, `inline code`; `<think>` tags are stripped automatically.
- **Regenerate** the last assistant reply in one click.
- **Conversation search** — *More › Search in the conversation*; non-matching messages dim to
  20 % opacity.
- **Session persistence & export** — sessions auto-save with a 4–5 word AI-generated title;
  export to `.md` / `.txt` with a stats header (model, turns, tool calls, tokens, duration).
- **Conversation branching** — `/branch <n>` forks the conversation at turn *n*: the branch keeps
  turns 1..*n* and the conversation continues in it, while the original is written back to disk
  first — exactly as it stands, under a generated name if it never had a file — so branching can
  never lose the half left behind.
  `/branch` lists the branch points and the family tree, `/branch <name>` switches branch.
  A branch is a plain session file with a `parent` + `fork_turn` link — nothing else in the
  store, the history rebuild or the picker had to learn about branching.
- **Sound notification** — an audible ping when a run finishes after more than 30 seconds (Visual Studio; turned off in Settings › Language and appearance › Agent runs).
- **Code snippet library** — ⭐ saves any code block; `/snippets` manages it across sessions.
- **Session & prompt templates** — `/template` loads a preconfigured context; user prompt
  templates support `{args}`.
- **Prompt history** — persistent; search with `/phistory`.
- **Welcome screen** — an empty session shows one-click cards for what can be done now: with a
  file open, explain it, write its tests, find its usages; without one, explain the project, review
  your uncommitted changes, see all commands. When the last build failed (Visual Studio) or the
  Problems panel has errors (VS Code), **Fix them** sends those errors to the agent.
- **Documentation** — the chat's **?** button (Visual Studio's chat header; beside the gear in VS Code's chat title
  bar, or *Inferpal: Documentation* in the command palette) and the last line of `/help` open these pages, as they
  were for the version installed.
- **Attach file / selection** — 📎 toolbar buttons attach a file or the current editor
  selection as a context chip (the same context you can add with `/read` or `@file`).
  The message bubble names what went with it, so the transcript — on screen, exported, or
  reloaded later — still says what the question was asked about.

## Models & hardware

- **Model manager** — `/models list/pull/delete/running` (streaming pull on Ollama).
- **VRAM monitoring** — a header badge shows the models resident in VRAM;
  `ModelLifetimeService` auto-unloads idle ones.
- **Hardware profile** — `/hardware` reports budget, loaded models, headroom, and a
  recommended `num_ctx`.
- **Model bench** — `/bench [model…]` runs a local test bench of installed models: warm
  time-to-first-token, tokens/s, VRAM pressure and a 5-task quality micro-eval scored by
  programmatic assertions (no LLM judge), with per-role recommendations (agent / utility /
  FIM) feeding the Model Router. `/bench last` redisplays the persisted run. The tokens/s figure
  only aggregates the tasks whose generation could actually be timed — a reply that never streams
  (a tool call) is left out rather than counted as free tokens — and the column shows `—` when
  nothing in the run could be timed.
- **Model arena** — `/arena <prompt>` sends the same prompt to two models (sequentially — one
  GPU) and shows both answers blind-labelled A/B; `/arena a|b|tie` records the vote and
  reveals the models, `/arena stats` shows the cumulative local standings.
- **Model Router** — a dedicated **utility model** role (`utilityModel`) handles session
  titles, `/commit` message proposals and compaction summaries instead of the chat model.
  With none set, they go to the chat model — or, when it is not loaded and the agent or
  code-actions model is, to that one: a session title never loads a model of its own.
  Opt-in **auto mode** (`modelRouterAuto`): with no utility model set, background tasks use
  the model `/bench` recommended for the utility role — but only when it is already warm in
  VRAM; a cold model is never loaded for a title or commit message.
- **Dynamic timeout engine** — Quick / Normal / Deep thresholds per task complexity.
- **Heartbeat & connection guard** — a silent pre-flight before every send; the Send button
  greys out when the server is unreachable, and recovers automatically.

## Safety

- **Workspace-confined file operations** — a single `AssertUnderRoot` sandbox on every
  path-taking tool. In Visual Studio the workspace is the folder that holds the whole solution:
  when a solution lists projects beside its own folder (`UI\UI.sln` naming `..\DAL\DAL.csproj`),
  the root widens to hold them — within the git repository that holds the solution, never to your
  home folder or a drive root, and not while the solution's folder keeps its own `.inferpal/`
  files (move them up to the wider folder to let it widen). Projects still out of reach are
  marked by `get_solution_info` and named in `/diagnostics`. A folder opened without a solution
  (*File › Open › Folder*) is the workspace too, unless it is a whole drive or your user folder.
  With no workspace — a file alone, or the few seconds before Visual Studio reports its solution —
  no tool reads or writes a file, builds, runs tests or runs a command in a folder nobody named:
  the refusal says to open a folder or a solution. `run_command` is not path-confined: when it
  runs anywhere but the workspace root, its approval card names the folder.
- **Markup and generated code** — a rename follows the markup that binds the symbol (`.razor`,
  `.cshtml`, `.xaml`: bindings under a matching `x:DataType`, `Model.X` and `asp-for`, a component's
  own code, route parameter and attributes on its tag) and lists every other occurrence of the
  name instead of rewriting it; `analyze_code impact` lists the markup that uses a type. A file a
  generator writes is refused for hand edits, with the source to change instead; an edit of the
  Windows Forms designer's code goes to a card that warns ([Tools](tools.md)).
- **Approval prompts** — `write_file`, `apply_diff`, `apply_edits`, `delete_file`,
  `run_command`, `rename_symbol`, `fetch_url`, `web_search`, custom shell tools, and MCP calls
  each prompt **Allow once / Always this session / Deny** ("Always" allows the tool for this session in this
  workspace, never written to disk). Edit prompts show the **actual diff** before you confirm. A command or a write that
  one narrow rule describes also offers that rule (*commands that start with `dotnet test`*,
  *writes under `tests/`*), written to the per-machine rules when picked; `/permissions suggest`
  names the ones you keep granting.
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
- **Files kept out of reach** — the ignore files a repository writes for AI tools are honoured:
  `.inferpalignore`, and JetBrains' `.aiignore`, Cursor's `.cursorignore`, Gemini's `.geminiignore`
  and `.aiexclude`, Codeium's, Cline's, Roo's, Continue's and Aider's, read at the workspace root in
  the gitignore syntax (negations, anchors, `**`, folders). What they exclude is never read nor
  changed by a tool, never attached (`@file`, `@folder`, chips, ACP attachments), indexed, pinned,
  completed by ghost text, sent by a code action (`/explain`, `/review`, `/fix`, `/refactor`, `/doc`,
  `/test`, Edit with AI) or shown in a git diff (`get_git_status`, `@diff`, `/check`, `/commit`) —
  and **every exclusion is said**: a refused call names the file, the line and the pattern, a walk
  says how many files it skipped, and `/check` and `/commit` say which files the model did not read.
  `ignoreFiles` chooses which files are read (empty = none). Not covered, by nature: what a command
  reads by itself (`run_command` can `cat` a file — the approval card shows the command), what an MCP
  server reads on its own, an ignore file in a subfolder, and the files written for the agent itself
  (`.inferpal/`, `AGENTS.md` and the other instruction files, skills), which are never excluded.
- **Instruction files always ask first** — a write to a file agents read back as instructions is
  force-prompted the same way: Inferpal's own `.inferpal/` memory, notes, context and rules, and the
  files a repository writes for coding agents (`AGENTS.md`, `CLAUDE.md`,
  `.github/copilot-instructions.md`, `.github/instructions/`, `.claude/rules/`, `.cursor/rules/`,
  `.cursorrules`, `.clinerules`, `.roo/rules/`, `.continue/rules/`). A silent edit there would
  change what every later session — and every other agent in the repository — is told to do.
- **Hardened SSRF guard** on outbound fetches (DNS rebinding, IPv4-mapped IPv6, `0.0.0.0/8`,
  loopback/private ranges, ReDoS-safe timeout).
- **Circuit breaker** on backend failures and **loop detection** to stop infinite agent loops.
- **Local diagnostics, transparent export** — `/diagnostics` lists background errors swallowed
  best-effort (in-memory ring buffer, zero telemetry), and `/diagnostics export` copies a
  **sanitized support bundle** for GitHub issues: remote endpoints redacted, API key never
  included, profile and workspace paths masked. The chat shows exactly the text that was
  copied — you read what you are about to send, and nothing leaves the machine on its own.
- **`/diagnostics` also answers "why is my ghost text gone?"** — the Visual Studio half that lives
  inside `devenv.exe` (inline completions, inline-diff preview, the `/tdd` debugger driver) fails
  silently by nature: the chat is out-of-process and keeps working. Each of its load paths
  publishes a heartbeat, and `/diagnostics` names the one that is missing — in the chat and in the
  support bundle. It says so only on **proof of absence**, never on absence of proof: in VS Code,
  where there is no in-process half at all, it stays quiet.

## Localization & theming

- **10 UI languages**, following Visual Studio or overridden independently.
- **Theme awareness** — colors follow the editor's Light / Dark / Blue themes and Windows high
  contrast; **Density** (*Language and appearance*) draws the conversation comfortable or compact.

## Keyboard shortcuts

Alt+B and Alt+O are Visual Studio's; in VS Code, **Ctrl+Alt+I** opens the chat. Alt+M and Ctrl+Shift+I work in both. In
VS Code, Edit with AI is **Ctrl+Shift+Alt+I** on Linux (Ctrl+Shift+I formats the document there) and **Cmd+Shift+I** on
macOS, and the map **Ctrl+Alt+M** on macOS (Option+M types µ); with the editor focused, Ctrl/Cmd+Shift+I is Inferpal's,
not the developer tools' or another assistant's — every key can be changed in VS Code's Keyboard Shortcuts.

| Shortcut | Action |
|---|---|
| **Alt+B** / **Alt+O** | Open the Inferpal chat window from anywhere |
| **Alt+M** | Run `/map`: the project's architecture map |
| **Ctrl+Shift+I** | Inline Edit (Edit with AI) on the selection |
| **Enter** | Send the message |
| **Shift+Enter** | Insert a newline in the prompt |
| **Tab** / **Esc** | Accept / dismiss a ghost-text completion |
