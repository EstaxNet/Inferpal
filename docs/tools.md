# Tools

The agent completes tasks by calling tools. There are **30 built-in tools**, plus any
**user-defined shell tools** and any tools exposed by connected **[MCP](mcp.md) servers**.

## Built-in tools

| Tool | Required params | Description |
|---|---|---|
| `read_file` | `path`, `start_line?`, `end_line?` | Read a file; a long one comes back in pages of whole lines, each naming the `start_line` to read on. A binary file is named, not shown |
| `write_file` | `path`, `content` | Write/overwrite a file. **Approval** + snapshot + Smart Fix |
| `list_files` | `path`, `pattern?` | List files (glob, recursive; at most 300, fewer when their paths would not fit the context — the answer says how many it left out) |
| `search_in_files` | `path`, `pattern`, `file_pattern?` | Regex/text search (at most 100 matches, fewer when they would not fit the context — the answer names the files of those it left out) |
| `run_command` | `command?`, `working_directory?`, `background?`, `action?`, `id?` | Run a shell command — PowerShell on Windows, and on Linux/macOS when `pwsh` is on the PATH; bash otherwise (`sh` on a host without bash) ; cwd and `env` overrides persist across calls. **Approval**, configurable timeout. `background=true` starts a detached job and returns its `id`; `action` `poll` / `stop` / `list` reads, ends or lists jobs (`command` is required otherwise) |
| `apply_diff` | `path`, `old_content`, `new_content`, `occurrence?` | Find-and-replace (exact, then whitespace-tolerant fuzzy fallback). `occurrence`: `unique` (default) / `first` / `all`. When `old_content` matches nothing, the refusal quotes the closest line of the file and where it differs. **Approval** (shows the diff) + snapshot + Smart Fix |
| `apply_edits` | `edits[]` (`path`, `old_content`, `new_content`, `occurrence?`) | **Atomic** multi-file edit — all edits resolved first; nothing is written unless every edit matches. One approval (combined diff) + snapshot per file + Smart Fix |
| `restore_file` | `path`, `snapshot?` | Restore a file from the backups Inferpal kept of it (`.inferpal/history/`, or your user folder with `historyStorage: user`) |
| `delete_file` | `path` | Delete a file. **Approval** + snapshot before deletion |
| `get_diagnostics` | `path?` | `dotnet build` → MSBuild errors/warnings (90 s timeout) |
| `get_active_document` | — | Path + content of the file open in the editor; a long one comes back as its first page, naming the `start_line` from which `read_file` reads on |
| `get_open_editors` | — | All open files, active one marked `[active]` |
| `get_git_status` | `path?`, `include_diff?`, `diff_path?` | `git status`, last 20 commits, branches (most recent first, the current one always shown), diff summary with its total; long sections keep what fits and count the rest. The full diff is capped, and a cut names the files it left out — `diff_path` reads one of them |
| `get_debugger_state` | — | Break state when paused: reason, exception, call stack (`file:line`), locals (backs `@debugger`) |
| `debug_control` | `action`, `file?`, `line?` | Drives the debugger: `set_breakpoint` / `clear_breakpoint` / `list_breakpoints` / `start` / `continue` / `step_over` / `step_into` / `step_out` / `stop`. **Approval on `start` only** — it runs your program; the steps that follow observe an execution you already consented to. Finite step budget, and running out is reported. The breakpoints it sets are removed when the session stops; yours are never removed |
| `debug_inspect` | `action?` (`state` \| `evaluate`), `expression?` | Reads a paused debugger: stop reason, user call stack, locals, and arbitrary expression evaluation in the current frame. Values are the debugger's own rendering — read, never parsed. **Approval on `evaluate`** — an expression can call your program's code |
| `run_tests` | `path?`, `filter?`, `runner?`, `timeout_seconds?` | `dotnet test` / `pytest` (under the project's `.venv` or `venv` when there is one; a test file runs alone, from its project's root) / `npm test` / `cargo test` / `go test` (auto-detected; a `path` that does not exist is refused; a folder with no single .NET solution or project in it runs the one `get_diagnostics` would build, said above the report). `runner` also takes `jest`, `vitest`, `mocha`, `node` (run through npm); an unknown one is refused by name. `timeout_seconds` 1–1800 (0 or less = the maximum: there is no unlimited run), the clamp said |
| `fetch_url` | `url`, `max_chars?`, `start_char?` | Fetch a page as text; a long one comes back in windows, each naming the `start_char` to read on. **Approval**, SSRF-guarded |
| `web_search` | `query`, `max_results?` | DuckDuckGo search. **Approval** |
| `get_solution_info` | `path?` | Parse `.sln` / `.csproj` — projects, frameworks, packages; a large solution details the projects that fit and names the rest |
| `insert_at_cursor` | `text` | Insert text at the cursor in the active editor |
| `replace_selection` | `text` | Replace the active selection |
| `update_memory` | `content?`, `mode?` | Update `.inferpal/memory.md` (`append` default / `replace` / `clear`, which takes no `content`). Writing it changes the system prompt of every later session, so the prompt is **always** shown even if a rule would allow it + snapshot; a snapshot that cannot be saved leaves the memory untouched |
| `analyze_code` | `mode`, … | Unified analysis facade (see below) |
| `search_codebase` | `query`, `top_k?` | Semantic search over the indexed project. Offered to the model only while there is an index, or one being built: with semantic search off and no index, the model uses `search_in_files` instead. While the first indexing pass runs, results say how many files it has read so far |
| `search_docs` | `query`, `top_k?` | Semantic search over `@Docs` external documentation. Offered once some documentation is indexed (`/docs add`) |
| `generate_project_map` | `refresh?` | Namespace tree, types, dependencies, hotspots — on a large solution the largest namespaces, the rest counted; past 10 000 source files the shallowest are read, and the map says how many of how many (cached for two minutes; `refresh=true` rescans) |
| `tool_search` | `query` | Load MCP tools that wait on demand: keywords (matched against tool names and descriptions) or `select:name1,name2`; at most 5 per call, each returned with its schema and declared from the next request on, for the rest of the conversation. Offered only while an MCP server's tools are on demand — see **[MCP → Large tool sets](mcp.md#large-tool-sets-on-demand)** |
| `read_skill_file` | `skill`, `path`, `start_line?`, `end_line?` | Read a file of a skill invoked with `/skill` — confined to that skill's folder (a `..` path or a link that leaves it is refused), which may lie outside the workspace (`~/.claude/skills`). Paged like `read_file`. Offered only while the repository or the user has a skill |
| `rename_symbol` | `old_name`, `new_name`, `root?`, `file_pattern?`, `dry_run?`, `declaring_file?`, `declaring_line?` | Project-wide rename. On C# it renames the **symbol**, not the spelling: a method called `Handle` is renamed without touching the dozen unrelated `Handle` methods that share the name (compiler-resolved; falls back to syntax when no workspace is known). When the name designates several symbols, it lists them and renames nothing until `declaring_file` (and `declaring_line`) says which one. Other languages use a word-boundary regex. **Markup** (`.razor`, `.cshtml`, `.xaml`, `.axaml`) is matched by name, never compiled, and rewritten only where it binds the symbol: a `{Binding}` or `x:Bind` under an `x:DataType` that has the member (derived view models included), a type through the `clr-namespace` its prefix maps to, `Model.X` and `asp-for` under an `@model T` whose type has it, `@inject T Name` names, a component's own code, expressions and route parameter (`{itemId:int}`, in its case) and its attributes on `<X …>` elsewhere. A type or component written without its namespace is looked up as the compiler does, through the namespaces the file sees (`@using X`, `_Imports.razor`, `_ViewImports.cshtml`, `@namespace X`, the project's root namespace and folders): two `IndexModel`s or two `CatalogSearch` components in one workspace are told apart, and a name the file's usings do not tell apart is listed. Every other occurrence of the name — text, a comment, another component's member of the same name, a binding whose source is not declared — is left and **listed** with why, in the report and on the card. A member declared in a component's `@code` is found by its declaration (two components declaring the name are listed, like two C# symbols, and `declaring_file` can name the `.razor`); the name of a component is its file's, so renaming it is refused. A generated file the rename touches (`Resources.Designer.cs`, gRPC code) is renamed and named with its source, which keeps the old name. All-or-nothing: a file that cannot be written puts every other one back unchanged. **Approval** + snapshot — the approval shows every changed line, so it applies in one call; `dry_run=true` only previews. When `apply_edits` or `apply_diff` is used to rename by hand, a refusal names `rename_symbol` with its arguments, and a write that leaves the old name in other files says which |

### `analyze_code` modes

One facade replaces the former `trace_dependency` / `analyze_impact` / `trace_nexus` tools,
selected by `mode`:

| `mode` | Does |
|---|---|
| `callgraph` | Methods in a file and what they call (`direction`: callees / callers / both) |
| `impact` | Blast radius of changing a file — dependent files, tests, entry points. `symbol` names one **type** the file declares (a method name is refused, naming the types and pointing to `callgraph`). When you pass `symbol` on a **C#** file, the report adds an **exact references** section resolved by the compiler and labelled as such: the other sections match names, this one resolves them (on this code base a name shared by a dozen types matched 61 places for 3 real uses). A **Markup** section lists the `.razor`, `.cshtml` and `.xaml` files that name the type where markup references one (a prefixed XAML tag `<local:X …>`, a component's tag `<X …>` in Razor, `x:DataType`, `@model T`, `@inject T Name`, `typeof`) — matched by name, not compiled, and counted in the risk: a component used only as a tag is not "safe to refactor freely" |
| `nexus` | Cross-language bridges between C# and TS/JS (REST endpoints, JS interop, SignalR) |

Other parameters, all optional: `path?`, `root?`, `symbol?`, `depth?`, `direction?`, `focus?`, `bridges?`.

A file the workspace's ignore files exclude (`.aiignore`, `.cursorignore`… — `ignoreFiles` in
[Configuration](configuration.md)) is neither read nor changed by any tool: a call that names it is refused before it
runs, with the ignore file, the line and the pattern; a walk (`list_files`, `search_in_files`, the analysis) skips it and
says how many files it skipped, above its result.

## Approval model

Tools that touch the filesystem, run commands, or reach the network are gated by an approval
card:

> **Allow once** · **Always this session** · **Always: *the narrow rule*** · **Deny**

- "Always this session" allows that tool **for this session, in this workspace** — it is never written to disk, and
  opening another folder or solution asks again.
- **Always: …** — when one narrow rule describes the call, the card offers it beside "Always":
  *commands that start with `dotnet test`*, *writes under `tests/`*. Picking it writes that rule
  to the per-machine **Permission rules** setting (the button's tooltip shows the exact lines), says
  so in the conversation, and the calls it covers are then approved without a card — after a
  restart too, and in the other editor. `/permissions` lists it; the settings remove it. An ACP
  client shows it as one more option.
  - A **command** rule covers one command that starts with the same program and subcommands
    (`dotnet test`, `git log`, `npm run build`, `python -m pytest`, `npx eslint`), with any
    arguments that keep it one command: no chain, pipe, substitution or redirection —
    `dotnet test && rm -rf src` still has its card. ⚠ The rule does not read the options: one
    that writes elsewhere or runs a program (`git diff --output=…`, `dotnet build -o …`) is
    covered like any other, as with any prefix rule. Pick it for the commands you would approve
    with whatever options the agent adds.
  - None is offered for a program that runs whatever it is given (`python script.py`, `node`,
    `bash`, `pwsh`, `cmd`, `npx` without a package, `npx tsx`, `uv run`, `docker run`, `sudo`,
    `find`, `sed`…), a program that deletes, overwrites, moves or reaches another machine (`rm`,
    `del`, `mv`, `cp`, `tar`, `chmod`, `curl`, `ssh`…), a subcommand that rewrites history, throws
    work away, installs or publishes, or changes what later commands run (`git push`,
    `git reset`, `git checkout`, `npm install`, `pip install`, `npm exec`, `npm publish`,
    `git config`…), a subcommand whose own subcommands the arguments would choose (`git stash`
    covers `git stash drop`, `dotnet nuget` covers `dotnet nuget push`), a program alone that
    needs its subcommand to say what it does (`dotnet`, `git`, `npm`…), a first word that is not
    a program name (a quoted path, `FOO=1 …`), or a call whose prompt is forced (instruction
    files, indirect execution, a repository's own commands).
  - A **write** rule covers the folder of the file — of every file of the change, when they share
    one — and what is under it, for `write_file`, `apply_diff` and `apply_edits`; never the
    workspace root, a folder outside the workspace or reached through a link, nor any folder
    whose name starts with a dot (`.inferpal/`, `.git/`, `.github/`, `.vscode/`: settings, hooks
    and workflows run later). Instruction files under it keep their card.
- `/permissions suggest` lists the narrow rules you granted at least three times and never
  refused — counted per rule in `approval-history.json`, beside the settings: the prefix or the
  folder, never the whole command; a card retired with a stopped run is not a refusal — in the
  order they were first counted, and `/permissions add <n>` writes one, named in its reply.
  Nothing is written otherwise. A rule is suggested only as a card would offer it now, in this
  workspace.
- Default action is *Allow once*; dismissing the prompt denies the call.
- For file edits (`write_file`, `apply_diff`, `apply_edits`, `delete_file`), the prompt shows
  the **actual diff** so you confirm the change, not just a path.
- An edit that would **leave a syntax error** (`write_file`, `apply_diff`, `apply_edits`) is refused
  before the prompt, and the model is told the file, the line and the error: C# by the compiler's
  parser (your project's symbols aside), Razor by its braces, Python by the Python of the environment you activated, else the first on your PATH (never a
  `.venv` found in the repository, which this check would run before your approval), `.mjs` and
  `.cjs` files by `node --check`. Only an error the file did not have is refused, never one the
  code only shows once compiled (a type not found yet). A file that did not parse before, a `.js`
  file (whether it parses depends on your project: modules, JSX), and a check that cannot run (no
  Python or Node, past 5 seconds) let the edit through. The same edit sent again after its refusal
  is written: the check may not know a language version your project uses.
- A file with **unsaved changes** in the editor is never written over, in Visual Studio as in VS
  Code: the edit is refused before the prompt, and checked again after you approve — so what you
  type while the prompt waits is kept. Save the file and ask again. `read_file` shows the unsaved
  text.
- A **generated** file is never edited by hand (`write_file`, `apply_diff`, `apply_edits`): one
  with an `<auto-generated>` header (XAML and Razor `*.g.cs`, `Resources.Designer.cs`, Entity
  Framework migration metadata and model snapshots, gRPC code) is refused before the prompt,
  naming where the change goes — its `.xaml`, `.resx` or `.proto`, a source generator's input, a
  new migration. A migration's own `Up`/`Down` file is written by hand and stays editable. A
  Windows Forms `*.Designer.cs` goes to a card that warns, and that no rule or "always" approves:
  removing a handler that no longer exists is fixed there, a change to the form belongs in the
  designer.
- A file **saved** while the prompt waits (by you, by auto-save, by another program) is not
  written over either: the approved change was computed from the file as it was, so it is
  refused and the agent is told to read the file again.
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
deny  * \.env$                                       # never write secrets (reads: .inferpalignore)
```

- Format: `allow|deny <tool|*> <regex>`, one per line. The regex is matched against the raw
  command / file path. **First match wins.** A `#` after whitespace starts a comment, as above
  (to match a literal space then `#`, write `\s#`).
- A `run_command` line is judged **command by command** (`;`, `&&`, `||`, `|`, `&`): it is
  auto-approved only when every command on it is allowed, and denied when any one of them is —
  `git status; Remove-Item -Recurse src` is not approved by a rule written for `git`.
- `allow` auto-approves (no prompt); `deny` blocks the call outright (recorded in
  `/diagnostics`); no match falls back to the prompt.
- Rules judge the calls that ask for approval — writes, commands, fetches, MCP tools. **Reading never asks**, so a
  `deny` rule does not hide a file from `read_file` or a search: to keep a file away from every tool, reads included,
  list it in `.inferpalignore` (or another ignore file).
- Sources, evaluated in order: the committable workspace overlay `.inferpal/permissions.json`
  (`{ "rules": ["deny …", …] }`), then the per-machine **Permission rules** setting — where the
  narrow rule an approval card offers is written.
- **The workspace overlay can only restrict, never grant.** It ships inside the repository, so
  `allow` rules found there are ignored (and recorded in `/diagnostics`): a cloned project must
  never be able to switch off your approval prompt. Only the per-machine setting can
  auto-approve. `deny` rules from the overlay are honoured — a project tightening its own
  restrictions is always safe. **Writing the overlay or an ignore file always asks**, even after "Always": a rewrite
  that empties one lifts every restriction it held.
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

### Packages that do not exist

Local models invent package names — a plausible `Apache.Parquet`, `ibantools` on PyPI. Before the card, a package a
call **adds** — `dotnet add package`, `npm install <name>`, `pip install <name>`, `uv add`, or a dependency that
`write_file`, `apply_diff` or `apply_edits` writes into a `.csproj`, `Directory.Packages.props`, `package.json`,
`requirements*.txt` or `pyproject.toml` — is looked up on its **public registry**: nuget.org (and the local package
folders NuGet is configured with) through `dotnet package search`, the npm registry through `npm view`, PyPI directly.
A name the registry does not have is **refused before you are asked**, and the model reads the names that do exist
(*Packages that exist with close names: pdf-lib, …*).

- **Only public registries are ever sent a name.** The lookup happens before the card, and the name is the model's, so
  it never goes to a server a configuration chose. Each tool is first asked offline which sources apply
  (`dotnet nuget list source`, `npm config get registry`, the yarn and bun files, the pip and uv configuration — in the
  workspace and above it, up to its git repository's root): a project or a machine that restores from another source
  — a company feed, a scope registry, a private index — is not checked at all, and its calls go to the card as before.
- Only a certain "absent" refuses. A registry that does not answer, a tool that is not installed, a lookup slower than
  20 seconds, a command that picks its own source (`--index-url`, `--registry`, `--source`) or changes it (a `PIP_*` or
  `npm_config_*` variable, `dotnet nuget add source`, `npm config set`), a call that also writes a source (`nuget.config`,
  `.npmrc`, an `--extra-index-url` line, MSBuild `RestoreSources`), what the project serves itself (an npm, pnpm or uv
  workspace package, `[tool.uv.sources]`, its own name) — the call goes to the card unchecked, and `/diagnostics` keeps
  why.
- A command is asked from where it runs, with the session's environment: a `cd` or a variable set by an earlier
  `run_command` counts, and so does the project it names (`dotnet add src/App.csproj package`, `npm --prefix web`).
- NuGet's search leaves out unlisted packages and lags behind new ones: an absent name is confirmed on the package's own
  nuget.org index. Prerelease-only packages count as existing.
- Names only: a version that does not exist is reported by the restore itself, and the model repairs it.
- Not checked: a write at the editor's cursor (`insert_at_cursor`, `replace_selection`), your own shell tools, MCP tools,
  and a command you type yourself (`/run npm install …`) — you know whether the name exists. Nor a call your
  [permission rules](#permission-rules-allow--deny-by-pattern) refuse: the rule decides first, and no name leaves.
- A network share among NuGet's sources (`\\server\feed`) counts as a server: the project is not checked.
- Untick *Check that a package exists before it is added* (Settings → Agent and approvals) to send nothing: every such
  call then goes to the card unchecked.

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

## Turning a tool off

Every tool's definition goes with every request, before the first word of the answer: the built-in ones alone take more
than half of an 8K window. `/tools` lists every tool with what its definition weighs and what the next request does
with it — sent, on demand, turned off; `/tools off <tool>` turns one off in every conversation, `/tools on <tool>` turns
it back on, and the **Tools** page of the settings has a button per tool. A built-in or custom tool is named by its own
name (`run_command`), an MCP tool as `server/tool` (its name without the prefix is accepted when one server only has
it). The list is the `disabledTools` setting.

- A tool turned off is never offered to the model and counted nowhere (the context gauge, X-Ray, the tenth of the window
  MCP tools may take). If the model calls it anyway, from memory, the call is refused and not run, and the answer names
  `/tools on`. X-Ray names the tools turned off.
- Only the model loses it: your own commands that use the tool — `/diff`, `/git`, `@problems`, `/tdd`'s test run — keep
  working.
- `tool_search` cannot be turned off: it is how the model reaches the MCP tools on demand.
- A line that names no tool served here — its MCP server is stopped, or only the other editor has the tool — is listed
  apart ("turned off, not available here now") and turned back on the same way.
- A server whose name holds a space: designate its tools by the name the model calls them (`mcp__My_Server__tool`),
  which `/tools` shows for them.
- An MCP server's entry can turn some of its tools off too (`disabledTools`, Roo's field) — see
  **[MCP](mcp.md#large-tool-sets-on-demand)**.
- `/tools off` and `/tools on` without a name still switch tool calling for the current conversation only.

## MCP tools

When MCP is enabled, every connected server's tools appear as `mcp__<server>__<tool>` and go
through the same approval prompt. When they would take more than a tenth of the context window, the heaviest
servers' tools wait on demand and `tool_search` loads them. See **[MCP](mcp.md)**.

## Adding a built-in tool

See **[Development → Adding a tool](development.md#adding-a-built-in-tool)**.
