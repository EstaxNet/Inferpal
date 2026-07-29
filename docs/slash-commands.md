# Slash Commands

Type `/` in the prompt to open the autocomplete popup. Commands fall into two groups: **chat
commands** (handled in the tool window) and **code actions** (which run against the active
editor with tools disabled).

> [!TIP]
> `/help` lists everything available in your build.

## Conversation

| Command | Description |
|---|---|
| `/clear` | Save and clear the conversation (reloads the system prompt) |
| `/model <name>` | Switch the active chat model |
| `/tools on\|off` | Enable or disable tool calling |
| `/export` | Export the conversation to `.md` or `.txt` |
| `/help` | Show all available commands |

## Context & memory

| Command | Description |
|---|---|
| `/context` | Show the active `.inferpal/context.md` |
| `/xray` | Context X-Ray: interactive panel breaking down everything composing the system prompt (base, custom, pinned, project files, scoped rules) — token bars, exact content per section, per-section on/off toggle for the next turn, copy of the raw prompt. Also opens by clicking the context gauge. Headless adapters get the markdown breakdown instead |
| `/memory` | Show `.inferpal/memory.md` (the agent's persistent memory) |
| `/note <text>` | Append a timestamped note to `.inferpal/notes.md` |
| `/notes [clear]` | List or clear all project notes |
| `/read <path>` | Attach a file as context |
| `/diff` | Attach the current `git diff` as a context chip |

## Files & shell

| Command | Description |
|---|---|
| `/ls <path> [pattern]` | List files in a directory |
| `/grep <dir> <pattern> [ext]` | Search text in files |
| `/run <command>` | Run a PowerShell command (requires approval) |
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
| `/setup` | Re-run first-run discovery: auto-detect the backend and auto-select chat + embedding models |

## Governance

| Command | Description |
|---|---|
| `/rules [init]` | List rules in `.inferpal/rules`, or scaffold an example |
| `/checks [init]` | List checks in `.inferpal/checks`, or scaffold an example |
| `/check [name\|init]` | AI-review the current git diff against the checks (100% local); `<name>` runs one |

## Agent

| Command | Description |
|---|---|
| `/agent-step` | Toggle agent step mode (pause between tool calls) |
| `/resume` | Resume the agent after a step-mode pause |
| `/plan` | Toggle plan mode — read-only: the agent explores and proposes a plan without editing files |

## History

| Command | Description |
|---|---|
| `/history [term]` | List saved sessions, or full-text search across them |
| `/phistory [term]` | Search prompt history; `/phistory use <n>` to reuse an entry |

## Code actions

These run against the active editor selection with tool calling disabled (see
[Tools](tools.md) and the editor context menu).

| Command | Description |
|---|---|
| `/explain` | Explain the active code — read-only, answers in the chat |
| `/fix` | Fix bugs in the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
| `/review` | Review the active code — read-only, answers in the chat |
| `/refactor` | Refactor the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
| `/test` | Generate unit tests into a **separate test file** (created/opened, or extended if it exists) |
| `/doc` | Add an XML documentation comment to the active code — **applied directly in the editor** (undoable with Ctrl+Z) |
