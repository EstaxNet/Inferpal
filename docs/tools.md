# Tools

The agent completes tasks by calling tools. There are **28 built-in tools**, plus any
**user-defined shell tools** and any tools exposed by connected **[MCP](mcp.md) servers**.

## Built-in tools

| Tool | Required params | Description |
|---|---|---|
| `read_file` | `path` | Read the full content of a file |
| `write_file` | `path`, `content` | Write/overwrite a file. **Approval** + snapshot + Smart Fix |
| `list_files` | `path`, `pattern?` | List files (glob, max 300, recursive) |
| `search_in_files` | `path`, `pattern`, `file_pattern?` | Regex/text search (max 100 results) |
| `run_command` | `command`, `working_directory?` | Run a shell command — PowerShell on Windows, bash on Linux/macOS ; cwd and `env` overrides persist across calls. **Approval**, configurable timeout |
| `apply_diff` | `path`, `old_content`, `new_content`, `occurrence?` | Find-and-replace (exact, then whitespace-tolerant fuzzy fallback). `occurrence`: `unique` (default) / `first` / `all`. **Approval** (shows the diff) + snapshot + Smart Fix |
| `apply_edits` | `edits[]` (`path`, `old_content`, `new_content`, `occurrence?`) | **Atomic** multi-file edit — all edits resolved first; nothing is written unless every edit matches. One approval (combined diff) + snapshot per file + Smart Fix |
| `restore_file` | `path`, `snapshot_path?` | Restore a file from `.inferpal/history/` |
| `delete_file` | `path` | Delete a file. **Approval** + snapshot before deletion |
| `get_diagnostics` | `path?` | `dotnet build` → MSBuild errors/warnings (90 s timeout) |
| `get_active_document` | — | Path + content of the file open in VS |
| `get_open_editors` | — | All open files, active one marked `[active]` |
| `get_git_status` | `path?`, `include_diff?` | `git status`, last 20 commits, branches, diff summary |
| `get_debugger_state` | — | Break state when paused: reason, exception, call stack (`file:line`), locals (backs `@debugger`) |
| `debug_control` | `action`, `file?`, `line?` | Drives the debugger: `set_breakpoint` / `clear_breakpoint` / `list_breakpoints` / `start` / `continue` / `step_over` / `step_into` / `step_out` / `stop`. **Approval on `start` only** — it runs your program; the steps that follow observe an execution you already consented to. Finite step budget, and running out is reported |
| `debug_inspect` | `action?` (`state` \| `evaluate`), `expression?` | Reads a paused debugger: stop reason, user call stack, locals, and arbitrary expression evaluation in the current frame. Values are the debugger's own rendering — read, never parsed |
| `run_tests` | `path?`, `filter?`, `runner?`, `timeout_seconds?` | `dotnet test` / `pytest` / `npm test` / `cargo test` / `go test` (auto-detected) |
| `fetch_url` | `url`, `max_chars?` | Fetch a page as text. **Approval**, SSRF-guarded |
| `web_search` | `query`, `max_results?` | DuckDuckGo search. **Approval** |
| `get_solution_info` | `path?` | Parse `.sln` / `.csproj` — projects, frameworks, packages |
| `insert_at_cursor` | `text` | Insert text at the cursor in the active editor |
| `replace_selection` | `text` | Replace the active selection |
| `update_memory` | `content` | Update `.inferpal/memory.md` (append / replace / clear) |
| `analyze_code` | `mode`, … | Unified analysis facade (see below) |
| `search_codebase` | `query`, `top_k?` | Semantic search over the indexed project |
| `search_docs` | `query`, `top_k?` | Semantic search over `@Docs` external documentation |
| `generate_project_map` | — | Namespace tree, types, dependencies, hotspots (TTL-cached) |
| `rename_symbol` | `old_name`, `new_name`, `root?`, `file_pattern?`, `dry_run?` | Project-wide rename. On C# it renames the **symbol**, not the spelling: a method called `Handle` is renamed without touching the dozen unrelated `Handle` methods that share the name (compiler-resolved; falls back to syntax when no workspace is known). Other languages use a word-boundary regex. **Approval** + snapshot; `dry_run=true` by default |

### `analyze_code` modes

One facade replaces the former `trace_dependency` / `analyze_impact` / `trace_nexus` tools,
selected by `mode`:

| `mode` | Does |
|---|---|
| `callgraph` | Methods in a file and what they call (`direction`: callees / callers / both) |
| `impact` | Blast radius of changing a file — dependent files, tests, entry points. When you pass `symbol` on a **C#** file, the report adds an **exact references** section resolved by the compiler and labelled as such: the other sections match names, this one resolves them (on this code base a name shared by a dozen types matched 61 places for 3 real uses) |
| `nexus` | Cross-language bridges between C# and TS/JS (REST endpoints, JS interop, SignalR) |

Other parameters: `path`, `root`, `symbol`, `depth`, `direction`, `focus`, `bridges`.

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
  formatting, fork bombs, …) always applies — even with security alerts disabled. It is an
  **accident guard, not a security boundary**: it matches submitted text, so obfuscation
  defeats it by construction. The actual boundary is the approval prompt, where the raw
  command is visible.
- **Force-prompt** on indirect execution — PowerShell (`iex`, `-EncodedCommand`,
  `FromBase64String`, `[scriptblock]::Create`, `& $var`) and POSIX (`eval`, piping into a
  shell, `base64 -d`, `sh -c "$var"`, `source`/`exec` on a variable) alike: what runs is
  not the text the rules read, so no auto-approval path applies (allow rule, session grant,
  security alerts disabled) — the call is never blocked, it simply always reaches the
  approval prompt.

See **[Configuration → Permission rules](configuration.md)**.

> [!NOTE]
> Setting **Disable security alerts** auto-approves the calls that would otherwise prompt. The
> built-in catastrophic-command denylist still applies.

## Custom shell tools

Expose your own shell commands as agent tools in **Settings → Custom agent tools**, one per
line:

```
name=command
```

Each becomes a native tool (lower-cased, spaces → `_`) and requires approval on every call.
Built-in tools take priority over a custom tool with the same name; prefix a line with `#`
to disable it.

## MCP tools

When MCP is enabled, every connected server's tools appear as `mcp__<server>__<tool>` and go
through the same approval prompt. See **[MCP](mcp.md)**.

## Adding a built-in tool

See **[Development → Adding a tool](development.md#adding-a-built-in-tool)**.
