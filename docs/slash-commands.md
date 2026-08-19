# Slash Commands

Type `/` in the prompt to open the autocomplete popup. Commands fall into two groups: **chat
commands** (handled in the tool window) and **code actions** (which run against the active
editor with tools disabled).

> [!TIP]
> `/help` lists everything available in your build.

> [!NOTE]
> **VS Code:** nearly all commands below also work in the VS Code extension, served headless
> by the host process (same approval and permission pipeline; long commands such as `/tdd`,
> `/bench`, `/models pull` and `/docs` are cancellable). Still VS-only: `/fix-build` and
> `/setup`, both coupled to Visual Studio by construction (the MSBuild banner, the live
> model-pull bubble) — they answer "not available in this editor yet".

## Conversation

| Command | Description |
|---|---|
| `/clear` | Save and clear the conversation (reloads the system prompt) |
| `/model <name>` | Switch the active chat model |
| `/tools on\|off` | Enable or disable tool calling |
| `/export` | Export the conversation to `.md` or `.txt` |
| `/help` | Show all available commands |
| `/diagnostics [clear\|on\|off\|export]` | List the background errors swallowed best-effort (in-memory ring buffer, includes permission-rule denials); `clear` empties it, `on`/`off` toggles the opt-in file log; `export` copies a **sanitized support bundle** to the clipboard for GitHub issues — versions, OS, provider (remote endpoints redacted, API key never included), model roles, feature toggles and the recent diagnostics with profile/workspace paths masked. The chat shows exactly what was copied: you read what you are about to send, and nothing leaves the machine on its own |

## Context & memory

| Command | Description |
|---|---|
| `/context` | Show the active `.inferpal/context.md` |
| `/xray` | Context X-Ray: interactive panel breaking down everything composing the system prompt (base, custom, pinned, project files, scoped rules) — token bars, exact content per section, per-section on/off toggle for the next turn, copy of the raw prompt. Also opens by clicking the context gauge. Headless adapters get the markdown breakdown instead |
| `/memory` | Show `.inferpal/memory.md` (the agent's persistent memory) |
| `/onboard` | Report the committed project profile (`.inferpal/project.json`): what it applied, what it recommends, what was refused |
| `/onboard init` | Write a commented example profile |
| `/onboard apply` | Write the profile's recommendations into **this machine's** settings — the only path from "recommended" to "in effect" |
| `/onboard context [force]` | Draft `.inferpal/context.md` from the repository (layout, README, recent commits). Refuses to overwrite an existing file without `force` |
| `/note <text>` | Append a timestamped note to `.inferpal/notes.md` |
| `/notes [clear]` | List or clear all project notes |
| `/read <path>` | Attach a file as context |
| `/diff` | Attach the current `git diff` as a context chip |

## Files & shell

| Command | Description |
|---|---|
| `/ls <path> [pattern]` | List files in a directory |
| `/grep <dir> <pattern> [ext]` | Search text in files |
| `/run <command>` | Run a shell command — PowerShell on Windows, bash on Linux/macOS (requires approval) |
| `/fetch <url>` | Fetch a web page as text (requires approval) |
| `/search-web <query>` | DuckDuckGo web search (aliases `/search`, `/web_search`, requires approval) |
| `/search-code <query>` | Semantic search across the indexed codebase (alias `/codebase`) |

## File history

| Command | Description |
|---|---|
| `/restore <path> [snapshot]` | Restore a file from `.inferpal/history/` (latest snapshot by default) |
| `/undo-run [list]` | Revert every file changed during the last agent run — restores edited files, deletes files created that run; `list` shows this session's tracked runs |
| `/replay [n]` | Post-mortem timeline of an agent run: every tool call with its target and duration, then the files it touched (`n` = nth most recent run, default latest) |

## Build, git & analysis

| Command | Description |
|---|---|
| `/build [path]` | Run a build and display errors |
| `/fix-build [path]` | Compile → AI fixes errors → recompile, until clean (max 5 rounds) |
| `/solution [path]` | Display the solution structure |
| `/map [path]` | Show the call graph of a file (`analyze_code mode=callgraph`) |
| `/git [path]` | Show git status, log, branches, diff summary |
| `/commit` | Generate an AI commit message from `git diff` (pre-fills the prompt) |
| `/commit-exec` | Execute the commit proposed by `/commit` |

## Knowledge & indexing

| Command | Description |
|---|---|
| `/index` | Start / restart background codebase indexing |
| `/index rebuild` | Force a full rebuild of the semantic index |
| `/docs add <url> [title]` | Crawl & index an external documentation site for `search_docs` |
| `/docs list \| remove <id> \| reindex [id]` | Manage indexed documentation sources |
| `/snippets` | `list` / `copy <n>` / `delete <n>` / `clear` saved code snippets |
| `/template [id]` | Load a session template (code-review / bug-hunt / architecture / refactoring / tests) |
| `/prompts [init]` | List reusable prompt files in `.inferpal/prompts/*.md`, or scaffold an example |

## Models & hardware

| Command | Description |
|---|---|
| `/models` | List / pull (streaming, Ollama) / delete / show running models |
| `/hardware [gb]` | Show the GPU/VRAM profile; `/hardware <gb>` sets the VRAM budget |
| `/bench [model…]` | Local test bench of installed models (default: all, capped at 5) — warm TTFT, tokens/s, VRAM pressure and a 5-task quality micro-eval scored by programmatic assertions (C# fix, instruction following, summary, tool call, FIM), plus per-role recommendations (agent / utility / FIM) feeding the Model Router; `/bench last` redisplays the persisted run |
| `/arena <prompt>` | Model arena: sends the same prompt to two models (sequentially — one GPU) and shows both answers blind-labelled A/B. Pair = chat model vs utility model, or explicit: `/arena <model1> <model2> <prompt>`. Vote with `/arena a\|b\|tie` (reveals the models), cumulative local standings with `/arena stats` |
| `/tdd [filter]` | "Fix until green": runs the test suite (`run_tests` — dotnet, pytest, npm, cargo, go), lets the agent patch the failing code, re-runs, up to 5 rounds. The optional filter narrows the run (`--filter` / `-k` / etc.). Twin of `/fix-build` on the test side; writes go through the usual approval pipeline and `/undo-run` applies |
| `/task <objective>` | Run an agent task **in the background**, detached from the conversation: submitting returns immediately and you keep working while it runs. Tasks run one at a time and wait for the chat to be idle before taking the GPU. **Read-only**: a background run explores and reports (`read_file`, `search_codebase`, `analyze_code`, …) but never writes, executes or reaches the network — so it can never interrupt you with an approval prompt. A notice appears in the chat when it finishes |
| `/task` · `/task list` | List the background tasks with their state (queued / running / done / failed / cancelled) |
| `/task <id>` | Show one task's full report, plus its step journal |
| `/task stop <id>` · `/task clear` | Cancel a task · forget the finished ones |
| `/task propose <objective>` | Same, but the task may **express** file changes: the editing tools are available to it and every edit is recorded instead of applied. The report comes back with a numbered diff per proposed change and nothing touched. Still no commands and no network |
| `/task apply <id> <n>` | Apply **one** proposal, through the usual approval prompt — so `/undo-run` covers it like any other write. There is deliberately no form that applies them all: approving a batch sight-unseen is what the read-only default exists to avoid. A file changed since the task ran is refused as stale rather than overwritten |
| `/setup` | Re-run first-run discovery: auto-detect the backend and auto-select chat + embedding models |

## Governance

| Command | Description |
|---|---|
| `/rules [init]` | List rules in `.inferpal/rules`, or scaffold an example |
| `/checks [init]` | List checks in `.inferpal/checks`, or scaffold an example |
| `/check [name\|init]` | AI-review the current git diff against the checks (100% local); `<name>` runs one. Findings come back **anchored to the diff** — `file:line`, severity, grouped by file — and a location the diff does not confirm is labelled as such rather than presented as one |

## Agent

| Command | Description |
|---|---|
| `/agent-step` | Toggle agent step mode (pause between tool calls) |
| `/resume` | Resume the agent after a step-mode pause |
| `/plan` | Toggle plan mode — read-only: the agent explores and proposes a plan without editing files |
| `/plan save [name]` | Save the plan from the last answer to `.inferpal/plans/<name>.md` — committed with the code, so it survives `/clear`, a restart and a colleague's clone |
| `/plan list` | List this repository's plans with their progress |
| `/plan <name>` | Open a plan and make it the active one |
| `/plan next` | Show the next unfinished step of the active plan |
| `/plan done <n>` / `/plan undone <n>` | Tick or reopen a step — the file is edited surgically, your prose and notes are untouched |
| `/debug <hypothesis>` | Settle a question about runtime behaviour by observing it: the agent picks a place where the state decides, sets a breakpoint, starts the session (**you confirm** — it runs your program), then reads the stack, the locals and the expressions that discriminate. Both editors: Visual Studio drives its debugger in-process, VS Code drives the Debug Adapter Protocol |
| `/debug` | What the debugger is doing right now: paused where, and which breakpoints are set |
| `/debug stop` | End the debugging session |

## History

| Command | Description |
|---|---|
| `/history [term]` | List saved sessions, or full-text search across them |
| `/phistory [term]` | Search prompt history; `/phistory use <n>` to reuse an entry |
| `/branch` | List the branch points of the conversation (one per turn) and the family tree of the current session |
| `/branch <n>` | Fork the conversation at turn *n*: turns 1..*n* are kept, the conversation continues in the branch, the original is written back to disk first (under a generated name if it had never been saved) |
| `/branch <name>` | Switch to another session or branch |

## Code actions

These run against the active editor selection with tool calling disabled (see
[Tools](tools.md) and the editor context menu).

| Command | Description |
|---|---|
| `/explain` | Explain the active code — read-only, answers in the chat |
| `/fix` | Fix bugs in the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
| `/review` | Review the active code — read-only, answers in the chat |
| `/refactor` | Refactor the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
| `/test` | Generate unit tests into a **separate test file** (created/opened, or extended if it exists). In VS Code the whole file is the input (the host's editor port exposes no selection) and an existing test file is rewritten on disk rather than through an undoable editor edit |
| `/doc` | Add an XML documentation comment to the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
