# Tools

The agent completes tasks by calling tools. There are **28 built-in tools**, plus any
**user-defined shell tools** and any tools exposed by connected **[MCP](mcp.md) servers**.

## Built-in tools

| Tool | Required params | Description |
|---|---|---|
| `read_file` | `path`, `start_line?`, `end_line?` | Read a file; a long one comes back in pages of whole lines, each naming the `start_line` to read on. A binary file is named, not shown |
| `write_file` | `path`, `content` | Write/overwrite a file. **Approval** + snapshot + Smart Fix |
| `list_files` | `path`, `pattern?` | List files (glob, recursive; at most 300, fewer when their paths would not fit the context — the answer says how many it left out) |
| `search_in_files` | `path`, `pattern`, `file_pattern?` | Regex/text search (at most 100 matches, fewer when they would not fit the context — the answer names the files of those it left out) |
| `run_command` | `command?`, `working_directory?`, `background?`, `action?`, `id?` | Run a shell command — PowerShell on Windows, and on Linux/macOS when `pwsh` is on the PATH; bash otherwise (`sh` on a host without bash) ; cwd and `env` overrides persist across calls. **Approval**, configurable timeout. `background=true` starts a detached job and returns its `id`; `action` `poll` / `stop` / `list` reads, ends or lists jobs (`command` is required otherwise) |
| `apply_diff` | `path`, `old_content`, `new_content`, `occurrence?` | Find-and-replace (exact, then whitespace-tolerant fuzzy fallback). `occurrence`: `unique` (default) / `first` / `all`. **Approval** (shows the diff) + snapshot + Smart Fix |
| `apply_edits` | `edits[]` (`path`, `old_content`, `new_content`, `occurrence?`) | **Atomic** multi-file edit — all edits resolved first; nothing is written unless every edit matches. One approval (combined diff) + snapshot per file + Smart Fix |
| `restore_file` | `path`, `snapshot?` | Restore a file from `.inferpal/history/` |
| `delete_file` | `path` | Delete a file. **Approval** + snapshot before deletion |
| `get_diagnostics` | `path?` | `dotnet build` → MSBuild errors/warnings (90 s timeout) |
| `get_active_document` | — | Path + content of the file open in the editor; a long one comes back as its first page, naming the `start_line` from which `read_file` reads on |
| `get_open_editors` | — | All open files, active one marked `[active]` |
| `get_git_status` | `path?`, `include_diff?`, `diff_path?` | `git status`, last 20 commits, branches (most recent first, the current one always shown), diff summary with its total; long sections keep what fits and count the rest. The full diff is capped, and a cut names the files it left out — `diff_path` reads one of them |
| `get_debugger_state` | — | Break state when paused: reason, exception, call stack (`file:line`), locals (backs `@debugger`) |
| `debug_control` | `action`, `file?`, `line?` | Drives the debugger: `set_breakpoint` / `clear_breakpoint` / `list_breakpoints` / `start` / `continue` / `step_over` / `step_into` / `step_out` / `stop`. **Approval on `start` only** — it runs your program; the steps that follow observe an execution you already consented to. Finite step budget, and running out is reported. The breakpoints it sets are removed when the session stops; yours are never removed |
| `debug_inspect` | `action?` (`state` \| `evaluate`), `expression?` | Reads a paused debugger: stop reason, user call stack, locals, and arbitrary expression evaluation in the current frame. Values are the debugger's own rendering — read, never parsed |
| `run_tests` | `path?`, `filter?`, `runner?`, `timeout_seconds?` | `dotnet test` / `pytest` (under the project's `.venv` or `venv` when there is one; a test file runs alone, from its project's root) / `npm test` / `cargo test` / `go test` (auto-detected; a `path` that does not exist is refused). `runner` also takes `jest`, `vitest`, `mocha`, `node` (run through npm); an unknown one is refused by name. `timeout_seconds` 1–1800 (0 or less = the maximum: there is no unlimited run), the clamp said |
| `fetch_url` | `url`, `max_chars?`, `start_char?` | Fetch a page as text; a long one comes back in windows, each naming the `start_char` to read on. **Approval**, SSRF-guarded |
| `web_search` | `query`, `max_results?` | DuckDuckGo search. **Approval** |
| `get_solution_info` | `path?` | Parse `.sln` / `.csproj` — projects, frameworks, packages; a large solution details the projects that fit and names the rest |
| `insert_at_cursor` | `text` | Insert text at the cursor in the active editor |
| `replace_selection` | `text` | Replace the active selection |
| `update_memory` | `content?`, `mode?` | Update `.inferpal/memory.md` (`append` default / `replace` / `clear`, which takes no `content`). Writing it changes the system prompt of every later session, so the prompt is **always** shown even if a rule would allow it + snapshot; a snapshot that cannot be saved leaves the memory untouched |
| `analyze_code` | `mode`, … | Unified analysis facade (see below) |
| `search_codebase` | `query`, `top_k?` | Semantic search over the indexed project. Offered to the model only while there is an index, or one being built: with semantic search off and no index, the model uses `search_in_files` instead |
| `search_docs` | `query`, `top_k?` | Semantic search over `@Docs` external documentation. Offered once some documentation is indexed (`/docs add`) |
| `generate_project_map` | `refresh?` | Namespace tree, types, dependencies, hotspots — on a large solution the largest namespaces, the rest counted (cached for two minutes; `refresh=true` rescans) |
| `rename_symbol` | `old_name`, `new_name`, `root?`, `file_pattern?`, `dry_run?`, `declaring_file?`, `declaring_line?` | Project-wide rename. On C# it renames the **symbol**, not the spelling: a method called `Handle` is renamed without touching the dozen unrelated `Handle` methods that share the name (compiler-resolved; falls back to syntax when no workspace is known). When the name designates several symbols, it lists them and renames nothing until `declaring_file` (and `declaring_line`) says which one. Other languages use a word-boundary regex. All-or-nothing: a file that cannot be written puts every other one back unchanged. **Approval** + snapshot; `dry_run=true` by default |

### `analyze_code` modes

One facade replaces the former `trace_dependency` / `analyze_impact` / `trace_nexus` tools,
selected by `mode`:

| `mode` | Does |
|---|---|
| `callgraph` | Methods in a file and what they call (`direction`: callees / callers / both) |
| `impact` | Blast radius of changing a file — dependent files, tests, entry points. `symbol` names one **type** the file declares (a method name is refused, naming the types and pointing to `callgraph`). When you pass `symbol` on a **C#** file, the report adds an **exact references** section resolved by the compiler and labelled as such: the other sections match names, this one resolves them (on this code base a name shared by a dozen types matched 61 places for 3 real uses) |
| `nexus` | Cross-language bridges between C# and TS/JS (REST endpoints, JS interop, SignalR) |

Other parameters, all optional: `path?`, `root?`, `symbol?`, `depth?`, `direction?`, `focus?`, `bridges?`.

## Approval model

Tools that touch the filesystem, run commands, or reach the network are gated by a 3-way
prompt:

> **Allow once** · **Always allow this tool** · **Cancel**

- "Always allow" remembers that tool **for the session only** — it is never written to disk.
- Default action is *Allow once*; dismissing the prompt denies the call.
- For file edits (`write_file`, `apply_diff`, `apply_edits`, `delete_file`), the prompt shows
  the **actual diff** so you confirm the change, not just a path.
- Gated tools: `write_file`, `apply_diff`, `apply_edits`, `delete_file`, `run_command`,
  `rename_symbol`, `fetch_url`, `web_search`, custom shell tools, and every MCP tool call.

`fetch_url` and `web_search` are gated because they are the outbound channel of the *lethal
trifecta*; `fetch_url` additionally passes a hardened SSRF guard (blocks DNS rebinding,
IPv4-mapped IPv6, `0.0.0.0/8`, loopback/private ranges, with a ReDoS-safe timeout).

### Permission rules (allow / deny by pattern)

Before the prompt, each call is classified by **permission rules** so an agent can run
unattended without either prompting on every step or opening the door to anything:

```
allow run_command ^\s*(dotnet|git|npm|cargo|go)\b   # auto-approve common dev commands
deny  run_command (Remove-Item|rm\s+-rf)            # block these outright
allow write_file \.(cs|ts|js|py)$                   # auto-approve edits to source files
deny  * \.env$                                       # never touch secrets, any tool
```

- Format: `allow|deny <tool|*> <regex>`, one per line. The regex is matched against the raw
  command / file path. **First match wins.**
- `allow` auto-approves (no prompt); `deny` blocks the call outright (recorded in
  `/diagnostics`); no match falls back to the prompt.
- Sources, evaluated in order: the per-machine **Permission rules** setting, then the
  committable workspace overlay `.inferpal/permissions.json` (`{ "rules": ["deny …", …] }`).
- **The workspace overlay can only restrict, never grant.** It ships inside the repository, so
  `allow` rules found there are ignored (and recorded in `/diagnostics`): a cloned project must
  never be able to switch off your approval prompt. Only the per-machine setting can
  auto-approve. `deny` rules from the overlay are honoured — a project tightening its own
  restrictions is always safe.
- A built-in denylist of catastrophic shell commands (recursive root deletes, disk
  formatting, fork bombs, …) always applies — even with approvals switched off. It is an
  **accident guard, not a security boundary**: it matches submitted text, so obfuscation
  defeats it by construction. The actual boundary is the approval prompt, where the raw
  command is visible.
- **Force-prompt** on indirect execution — PowerShell (`iex`, `-EncodedCommand`,
  `FromBase64String`, `[scriptblock]::Create`, `& $var`) and POSIX (`eval`, piping into a
  shell, `base64 -d`, `sh -c "$var"`, `source`/`exec` on a variable) alike: what runs is
  not the text the rules read, so no auto-approval path applies (allow rule, session grant,
  approvals switched off) — the call is never blocked, it simply always reaches the
  approval prompt.

See **[Configuration → Permission rules](configuration.md)**.

> [!NOTE]
> Unticking *Ask me before changing files, running commands or going online* (Settings → Agent and approvals) auto-approves the calls that
> would otherwise prompt. The built-in catastrophic-command denylist still applies.

## Custom shell tools

Expose your own shell commands as agent tools in **Settings → Tools and MCP servers → Your agent
tools**, one per line:

```
name=command
```

Each becomes a native tool (lower-cased, spaces → `_`) and requires approval on every call. It runs in the
workspace root. Write `{args}` where the agent's arguments go (`run_e2e=npm run test:e2e -- {args}`); without it,
they are appended to the command.
Built-in tools take priority over a custom tool with the same name; prefix a line with `#`
to disable it.

## MCP tools

When MCP is enabled, every connected server's tools appear as `mcp__<server>__<tool>` and go
through the same approval prompt. See **[MCP](mcp.md)**.

## Adding a built-in tool

See **[Development → Adding a tool](development.md#adding-a-built-in-tool)**.
