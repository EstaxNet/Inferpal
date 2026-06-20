# Tools

The agent completes tasks by calling tools. There are **26 built-in tools**, plus any
**user-defined shell tools** and any tools exposed by connected **[MCP](mcp.md) servers**.

## Built-in tools

| Tool | Required params | Description |
|---|---|---|
| `read_file` | `path` | Read the full content of a file |
| `write_file` | `path`, `content` | Write/overwrite a file. **Approval** + snapshot + Smart Fix |
| `list_files` | `path`, `pattern?` | List files (glob, max 300, recursive) |
| `search_in_files` | `path`, `pattern`, `file_pattern?` | Regex/text search (max 100 results) |
| `run_command` | `command`, `working_directory?` | Run a PowerShell command. **Approval**, configurable timeout |
| `apply_diff` | `path`, `old_content`, `new_content`, `occurrence?` | Find-and-replace (exact, then whitespace-tolerant fuzzy fallback). `occurrence`: `unique` (default) / `first` / `all`. **Approval** (shows the diff) + snapshot + Smart Fix |
| `apply_edits` | `edits[]` (`path`, `old_content`, `new_content`, `occurrence?`) | **Atomic** multi-file edit — all edits resolved first; nothing is written unless every edit matches. One approval (combined diff) + snapshot per file + Smart Fix |
| `restore_file` | `path`, `snapshot_path?` | Restore a file from `.inferpal/history/` |
| `delete_file` | `path` | Delete a file. **Approval** + snapshot before deletion |
| `get_diagnostics` | `path?` | `dotnet build` → MSBuild errors/warnings (90 s timeout) |
| `get_active_document` | — | Path + content of the file open in VS |
| `get_open_editors` | — | All open files, active one marked `[active]` |
| `get_git_status` | `path?`, `include_diff?` | `git status`, last 20 commits, branches, diff summary |
| `get_debugger_state` | — | Break state when paused: reason, exception, call stack (`file:line`), locals (backs `@debugger`) |
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
| `rename_symbol` | `symbol`, `new_name`, `path?`, `dry_run?` | Project-wide rename (Roslyn for C#, regex fallback). **Approval** + snapshot; `dry_run=true` by default |

### `analyze_code` modes

One facade replaces the former `trace_dependency` / `analyze_impact` / `trace_nexus` tools,
selected by `mode`:

| `mode` | Does |
|---|---|
| `callgraph` | Methods in a file and what they call (`direction`: callees / callers / both) |
| `impact` | Blast radius of changing a file — dependent files, tests, entry points |
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
  committable workspace overlay `.inferpal/permissions.json` (`{ "rules": ["allow …", …] }`).
- A built-in, **non-bypassable denylist** of catastrophic shell commands (recursive root
  deletes, disk formatting, fork bombs, …) always applies — even with security alerts disabled.

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
