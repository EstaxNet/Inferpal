# MCP — Model Context Protocol

MCP gives the agent tools Inferpal does not ship: a database, GitHub, a folder outside your workspace. This page adds a
first server in three steps, then takes the cases you meet next: a sign-in, the servers a repository declares, a server
that fails. The fine print waits in folds.

Inferpal can connect to any **stdio or Streamable HTTP [MCP](https://modelcontextprotocol.io)
server** and expose its tools to the agent — the same servers used by Claude Desktop and Continue
(filesystem, GitHub, databases, and hundreds more).

## Add your first server

1. **Settings → Tools and MCP servers** — tick *Use MCP servers*.
2. Paste a server map in the Claude Desktop / Continue format:

   ```json
   {
     "filesystem": {
       "command": "npx",
       "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\dev"],
       "env": {}
     },
     "remote": {
       "url": "https://mcp.example.com/mcp",
       "headers": { "Authorization": "Bearer ${MY_TOKEN}" }
     }
   }
   ```

   A server with a `command` uses the **stdio** transport; one with a `url` uses **Streamable HTTP**.
   The list editor handles both: tick **“HTTP server”** to switch a server between
   the command/arguments/environment fields (one argument per line) and the url/headers fields, or
   edit the raw map with **Edit as JSON**.

3. Save. Each server is started and shown as a card: *Connected · 11 tools*, *Needs sign-in*, *Off*,
   or *Did not start* — then with the server's own error message under it (what it wrote before
   exiting, or why it was refused) and a **Retry** button.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/features/settings-mcp-dark.png">
    <img src="assets/features/settings-mcp-light.png" alt="The MCP servers page of the settings: Use MCP servers ticked, Edit as JSON, and four server cards. github is connected with 6 tools; tickets needs sign-in, with a Sign in button; database did not start, with a Retry button and its reason, uvx-not-installed: not found on this machine; old-fs is off. Add a server sits under the list" width="740">
  </picture>
  <br><sub>One card per server: its state, the reason when it failed, and what you can do about it.</sub>
</p>

How its tools reach the agent:

- On startup (or after saving settings), each server is spawned and its tools are discovered
  via `tools/list`.
- Discovered tools appear to the agent as `mcp__<server>__<tool>` and are merged into the
  tool list alongside built-ins and custom shell tools (built-ins take priority on a name
  clash).

**Try it**

| Type | What happens |
|---|---|
| `/tools` | Every tool with what it weighs, the server's tools among them |
| `/diagnostics` | Why a server did not start, in the server's own words |

<details>
<summary><b>In detail: where a stdio server starts, npx on Windows, tokens in headers</b></summary>

- A stdio server starts in the folder its `"cwd"` names (`~` is your home folder), else in the editor's own.
- `"command": "npx"` (or `"npm"`) works as written on every OS: on Windows, where both are batch
  scripts, Inferpal runs them through the `node.exe` installed beside them — never through `cmd.exe`,
  so no `"cmd", "/c"` wrapper is needed.
- Header values support `${ENV_VAR}` expansion (resolved at connection time), so tokens stay out of
  the stored config.

</details>

## Every call asks first

Every MCP tool call is gated by the same approval card as the built-in tools:

> **Allow once** · **Always this session** · **Deny**

The "always" grant is **scoped to the session and the workspace, and never persisted** — MCP servers run
arbitrary external code, so the choice is deliberately not remembered across sessions. See
[Tools → Approval model](tools.md#approval-model).

## Sign in to a remote server (OAuth 2.1)

HTTP auth supports **static headers** (bearer token / custom headers, with `${ENV_VAR}` expansion)
**and OAuth 2.1** (see below).

When a Streamable HTTP server returns **401** and no credential of yours went with the request (no `Authorization`
or API-key header configured), it requires OAuth. A 401 to a request that carried your own header is the server
refusing that credential: the card shows **Did not start** with the server's reason, and names a `${VAR}` the editor's
environment does not set.

- The server's card shows **Needs sign-in** with a **Sign in** button, in both editors. Clicking it
  opens your browser to the provider's consent page and captures the redirect on a temporary loopback
  listener (`http://127.0.0.1:<port>/callback`). On success the tokens are stored and the server
  reconnects.
- In VS Code the page opens through VS Code — on your machine, with the redirect forwarded, also when the
  window is connected to a remote machine (SSH, WSL, a container). If no browser can be opened, the sign-in says so at
  once instead of waiting for a redirect that cannot come.
- Tokens are stored **encrypted** (Windows DPAPI per user, or the editor's secret store elsewhere) in `%AppData%/Inferpal/mcp-oauth.dat`
  — never in the config JSON. Access tokens are refreshed automatically; you only re-authorize when
  the refresh token is rejected.
- A provider that cannot answer at that moment (down, busy, unreachable) does not reject
  it: the server's status says the sign-in could not be renewed, and the next call tries again.
- Visual Studio and VS Code share the sign-in, so a token one of them renewed is used by the other.
- If the authorization server does **not** support dynamic registration, set a pre-registered client
  in the server's `oauth` block:

  ```json
  "remote": {
    "url": "https://mcp.example.com/mcp",
    "oauth": { "client_id": "your-client-id", "scopes": ["mcp.read", "mcp.write"] }
  }
  ```

<details>
<summary><b>In detail: discovery, scopes, what is checked before a code is used</b></summary>

- Inferpal implements the MCP
  authorization spec (2026-07-28): discovery of the authorization server (RFC 9728 → RFC 8414 or OpenID Connect
  discovery, an issuer with a path included — Entra, Keycloak, Okta), **PKCE**, the **`resource`** indicator
  (RFC 8707), and **dynamic client registration** (RFC 7591) when the server supports it.
- **The scopes asked for** are the ones in the server's `oauth` block; without them, the ones the MCP server's own
  metadata names; without those, none — never every scope the authorization server lists, which a shared identity
  provider would refuse or ask you to consent to in full.
- When no metadata document is found, Inferpal tries the
  conventional `/authorize` and `/token` at the authorization server's origin and notes it in `/diagnostics`.
- **The answer is checked before the code is used**: when the authorization server names itself in its answer
  (`iss`, RFC 9207), the name must be the one its metadata gives, exactly; one that advertises it and leaves it out is
  refused too. An answer from another authorization server is not used and its error is not shown — that is how a
  mix-up attack would deliver someone else's code.
- A client registered with one authorization server is reused only with that one: if the server moves to another,
  Inferpal registers again.

</details>

## Servers your repository declares

The MCP servers a repository declares for other tools start beside yours — once you agree:

| File | Written for | Root key |
|---|---|---|
| `.vscode/mcp.json` | VS Code (with its `inputs`) | `servers` |
| `.mcp.json` | Claude Code, VS Code's portable format | `mcpServers` |
| `.vs/mcp.json` | Visual Studio | `servers` |
| `.cursor/mcp.json` | Cursor | `mcpServers` |
| `.roo/mcp.json` | Roo Code | `mcpServers` |
| `.continue/mcpServers/*.yaml` | Continue (a list of named servers) | `mcpServers` |

```mermaid
flowchart LR
    F(["A repository file<br/>declares a server"]) --> K{"Already agreed, for this<br/>definition and its files?"}
    K -- "yes" --> S(["It starts"])
    K -- "no" --> A{"You are asked"}
    A -- "you agree" --> S
    A -- "you decline" --> O(["It stays off, asked<br/>again at the next start"])
```

- **Nothing runs before you agree.** A repository's server runs a program the repository chose, so it is asked about
  first — the server, its file and the command it runs — **even with approvals switched off and under an `allow`
  rule**. Your agreement is kept for that repository and that exact definition: change one argument and you are asked
  again. A declined server is asked about again at the next start.
- **The servers follow the branch.** Before each question, Inferpal looks at these files again: when a checkout, a
  pull or an edit changed one of them (or added one), the repository's servers are read again before the question —
  a changed definition is asked about again, a server the files no longer declare stops. No restart, no command.
- **Your agreement also covers the repository's files the server runs.** When one of these files changes, the server
  is asked about again. In a git repository, a checkout, a pull or a commit that changes them is seen before the next
  question: the servers are read again, you are asked about the new code, and it starts once you agree — the old code
  does not keep answering. An edit you have not committed is not followed there (a server you are writing yourself, or
  one that writes beside its code, would ask at every question). These files are checked once more just before the
  server starts, and before Inferpal restarts a server that exited: one whose files changed stays stopped, and its
  status names the file.
- A repository's server you agreed to — and a server your ACP client passes — has a card of its own in the settings,
  under your list, with its state and its cause. It cannot be edited from there (its definition is not yours), but an
  HTTP server that asks for a sign-in is signed in to there, with the same **Sign in** button as yours.

<details>
<summary><b>In detail: which files your agreement covers, and what it does not</b></summary>

- Inferpal looks for every file and folder of
  the repository the definition names — in its command, its arguments or its environment, variables replaced, and in
  the `package.json` script that `npm run` (or `pnpm`, `yarn`, `bun`) starts — and covers each named file with the
  folder it lives in, the `cwd` it declares, and the manifest its runner reads (`package.json`, `pyproject.toml`, a
  `.csproj`…).
- A name the runtime completes counts too: `node tools/mcp/server` covers `tools/mcp`, and
  `python -m tools.mcp.server` covers `tools/mcp/server.py`.
- In a git repository a folder means the files git
  **tracks** there — what a pull changes, the files of a submodule included — so what your machine writes beside the
  code (build output, installed dependencies, caches, logs, a database) never asks again; outside git, those folders
  are read whole, build output, dependency and cache folders excepted — and so is a folder git tracks nothing in (a
  workspace inside another repository's work tree, a name written in another case than git's).
- A file the definition
  names is always covered, even one git does not track: a database named in its arguments asks again when it
  changes.
- A link that leads out of the repository is never followed: the agreement covers where it points, not what lies
  there.
- Not covered — the command on the card is what you agree to there: what the program fetches or finds on its own
  (an `npx` package, a module on its own path), the files a build pulls in from elsewhere, and the imports of a script
  at the repository's root, which covers that file alone rather than the whole repository.
- A server whose folders hold more than 20,000 files, or whose files cannot be read, cannot be covered: it is asked
  about at every start, its status and `/diagnostics` say why, and it is not restarted after it stops.
- An agreement given by an earlier version of Inferpal to a server that runs repository files is asked again once.
  The editor still on that version keeps its own agreement, so the two editors do not undo each other's.

</details>

<details>
<summary><b>In detail: variables, where it runs, Continue's fields, name clashes</b></summary>

- **Variables** are replaced the way each tool does: `${workspaceFolder}`, `${workspaceFolderBasename}`,
  `${userHome}`, `${env:NAME}`, `${NAME}` and `${NAME:-default}`, `${input:id}` — asked once per session, in VS
  Code's input box (hidden when the input is a `password`) or Visual Studio's input prompt (which cannot hide what is
  typed, and says so). A variable nothing fills (`${{ secrets.X }}`, an unset variable) is named and the server does
  not start on an empty value.
- A repository's server **runs in the repository's root**, or in the `cwd` it declares, resolved against that root.
- Continue's remote server sends its `requestOptions.headers`, and its `apiKey` as `Authorization: Bearer …`.
- A
  server that declares the older SSE transport (`"type": "sse"`) is tried with Streamable HTTP, which many such servers
  also serve; when it does not, its error says that SSE is not supported.
- A server that declares WebSocket is not
  started — nothing in Inferpal speaks it — and its status says so; `/import` leaves it out by name.
- A server you configured yourself keeps its name: the repository's server of the same name, or one whose tool names could match yours, does not start, and the
  status says why. So does a file that cannot be read, with its reason.

</details>

## A server that asks you for information

A server of the 2026-07-28 revision can stop a tool call to ask **you** for values — a repository, a date, a choice —
through a form (*elicitation*, form mode). Inferpal shows it as a card in the conversation, in Visual Studio and in
VS Code:

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/features/mcp-form-dark.png">
    <img src="assets/features/mcp-form-light.png" alt="An MCP server asks for information on a card in the chat: How should version 1.9.0 be published? A version bump choice, a release date, and a field showing 45 refused with: A whole number from 1 to 30" width="420">
  </picture>
  <br><sub>A field to fix says what to fix, and nothing is sent until every field passes.</sub>
</p>

- The card says **which server asks**, for which tool, and the server's own message, as plain text.
- One field per value the server asks for: text (email, address, date, date and time), a number with its bounds, a
  box for yes or no, one choice (buttons side by side when there are few) or several (a box each). Required fields carry
  a `*`. What a server suggests is filled in; you can change everything before sending.
- **Send** (Enter in a field) sends the values — **checked first** against what the server asked: a field to fix says
  what to fix, and nothing is sent until every field passes. **Decline** tells the server you said no. **Cancel**
  (Esc) closes the question without answering; the server is told so.
- The card reminds you that **a server must never ask for a password, a key or a token** this way (the specification
  forbids it), and a field whose name reads like one says so under it.
- Only you answer a card: no rule, no "always", no setting answers it, and it never takes the keyboard on its own.
  Stop ends the run, the question unanswered; a note typed while the card waits sets it aside.
- Once answered, the card leaves a line in the conversation: what you did, and — folded under it — the values you sent.
  It is saved with the conversation. **The values go to the server only**: the model reads what the server did with
  them, and a line saying whether you answered, declined or cancelled.

Not offered: a server asking to open a web page (URL mode) — it is reported to the model, like a form the card cannot
draw (a nested field). A server is asked only where a card can be drawn: during a turn of the chat. An
[Agent Client Protocol](acp.md) client has no such card, so its servers are never offered forms.

## Large tool sets: on demand

Every tool's definition is sent with every request, before the first word of the answer. An editor's own server can
weigh more than the whole window: Rider's has 93 tools, about 49,000 tokens as Inferpal counts them, more than a 32K
window.

So the MCP tools are declared while they take at most **a tenth of the context window in use** (32,768 tokens
are assumed while the window is not known yet); beyond that, servers go **on demand** one at a time, the heaviest
first, until the rest fits — a small server stays declared beside a large one.

- **X-Ray** says which tools wait: "93 MCP tools on demand — not sent until loaded by tool_search: rider (93)".
- Plan mode and background tasks send no MCP tool at all, so `tool_search` is not offered there.
- **Tools you never want sent** are turned off one by one: `/tools off rider/execute_sql_query`, or the **Tools** page
  of the settings (see **[Tools → Turning a tool off](tools.md#turning-a-tool-off)**). A tool turned off weighs nothing
  in the tenth of the window and `tool_search` neither names nor loads it.
- A server's entry can list them as Roo writes
  it — `"disabledTools": ["execute_sql_query"]`, in `mcpServersJson` or a repository's `.roo/mcp.json` — and they are
  turned off the same way; `/tools` says "turned off by its server's entry", and that entry is where one is turned back on.

**Try it**

| Type | What happens |
|---|---|
| `/tools` | Every tool with what it weighs and what the next request does with it: sent, on demand, turned off |
| `/xray` | The tools that wait, by server |
| `/tools off rider/execute_sql_query` | That tool is turned off in every conversation |

<details>
<summary><b>In detail: how tool_search loads a tool, and how long it stays</b></summary>

- Their tools are not sent. The model gets one more tool, `tool_search`, whose description names the servers on demand
  and their tools (within the same tenth of the window; a server whose names do not fit is named with its count).
- It
  loads tools by name (`select:mcp__rider__get_run_configurations`, or the name without its prefix when only one server
  has it) or by keywords — the closest matches, the others named so they can be selected — at most five per call, each
  returned with its schema.
- A tool called directly by its name, as a structured call or written as text, is loaded once
  it has run.
- A loaded tool is declared from the next request on, **after every other tool, in the order it was loaded**, and never
  moves: a server that reuses its cache reads the request again only from the first change — once per load, never once
  per question.
- It stays loaded for the rest of the conversation; a new conversation starts without it.

</details>

## When a server's tools change, or it fails

**When a server does not start**, `/diagnostics` says why in the server's own words: a stdio server
that exits is reported with its exit code and the first and last lines it wrote on stderr (where a
server says it is missing a token, a path or a package), and an HTTP server that refuses is reported
with the reason in its response, not only the status code.

If a **stdio** server process **dies mid-session**, its tools are dropped immediately and Inferpal
auto-reconnects with backoff (1s → 2s → 5s → 10s → 30s). On success the tools reappear; if every
attempt fails the server is left disconnected until the next save, its status naming why the last
attempt failed.

The death itself is named in `/diagnostics` — exit code and stderr, once per cause —
so a server that crashes on every call and is restarted each time still leaves a trace.
(HTTP has no equivalent process-death signal; an ended GET stream is treated as a normal rotation
and simply re-opened.)

Servers that advertise the `tools.listChanged` capability are re-discovered **live**: when one
sends a `notifications/tools/list_changed`, Inferpal re-runs `tools/list` for that server and
republishes the merged tool set — no settings save needed. Servers
without that capability are refreshed when you save settings (or on startup).

<details>
<summary><b>In detail: how a server signals a change</b></summary>

- This works on **both transports**: stdio
  servers signal it on their stdout stream, HTTP servers on the optional server→client GET SSE stream
  (opened automatically after the handshake).
- A 2026-07-28 server sends it only on a subscription asked
  for, so Inferpal opens one (`subscriptions/listen`) when the server announces the capability.

</details>

## Limits to know

> [!NOTE]
> **Tool results are forwarded as text.** Inferpal passes on `text` blocks and the text of `resource`
> blocks. A tool that answers with an image, audio, a resource link or a binary blob has that block
> **named** in the result instead of forwarded — a local text model cannot read a PNG, but "the tool
> returned an image" and "the tool returned nothing" are not the same answer, and a screenshot server
> used to read as the second.

<details>
<summary><b>In detail: structured arguments, roots, sampling</b></summary>

- **A structured argument is written out in full.** A tool schema may describe an argument once and refer to it
  (`$defs` and `$ref` — how the Python SDK writes every nested model). Ollama keeps no `$ref`, so such an argument
  would reach the model with no type: Inferpal inlines the schema's local references before offering the tool. A
  reference to another document is left as written and never fetched.
- **A local server that asks for its roots gets the workspace folder.** A stdio server of the earlier revisions (the
  handshake Inferpal speaks with most servers today) is told Inferpal has `roots`, and `roots/list` answers the folder
  Inferpal works in (`file://…`, its name). Without an open folder nothing is declared, never an empty list.
- Not given:
  a remote server — it would be handed a path of your machine — and a server of the 2026-07-28 revision, which
  deprecates roots.
- ⚠ The reference `filesystem` server **replaces** the folders it was started with by the roots it is
  given, and in Inferpal it is mostly started for folders *outside* the workspace: a server whose command line already
  names a folder of its own keeps it and is given no roots. `"roots": true` in its entry gives it the workspace anyway,
  `"roots": false` keeps the workspace from any server.
- **Sampling is not answered.** Inferpal does not declare it, so a 2026-07-28 server may not ask for it; a tool that
  still does is reported to the model as *needing input Inferpal does not provide*, never as an empty result.
- A tool
  whose `x-mcp-header` annotation breaks the specification's rules is left out of the list, as the specification
  requires, and `/diagnostics` names it.

</details>

## How Inferpal talks to a server

For anyone writing or debugging a server. Inferpal first finds out which revision of the protocol the server speaks,
then lists its tools; a tool call comes after your approval.

```mermaid
%%{init: {"sequence": {"mirrorActors": false}}}%%
sequenceDiagram
    participant I as Inferpal
    participant S as MCP server
    I->>S: server/discover
    alt 2026-07-28 server
        S-->>I: supported versions, capabilities
    else earlier server
        S-->>I: error (or silence, on stdio)
        I->>S: initialize
        S-->>I: capabilities
        I->>S: notifications/initialized
    end
    I->>S: tools/list
    S-->>I: [tools]
    Note over I,S: agent calls a tool
    I->>S: tools/call (after approval)
    S-->>I: result
```

<details>
<summary><b>In detail: the client, its transports, the two eras of the protocol</b></summary>

- The client is a **home-grown JSON-RPC 2.0** implementation with **zero extra NuGet
  dependencies**, then `tools/list` / `tools/call`.
- Two transports implement the same `IMcpClient`
  contract: **stdio** (over the server process's stdin/stdout) and **Streamable HTTP** (POST to a
  single endpoint; the server replies with `application/json` or a `text/event-stream`).
- It speaks **both eras of the protocol**. It opens with `server/discover`: a server of the
  **2026-07-28** revision answers it, and is then spoken to without a handshake — every request
  carries its version and the client's identity in `_meta`, and over HTTP the `MCP-Protocol-Version`,
  `Mcp-Method`, `Mcp-Name` and the tool's own `Mcp-Param-*` headers.
- A server of an earlier revision
  refuses that request (or, on stdio, stays silent for 3 seconds), and gets the `initialize`
  handshake it has always received — over HTTP, with its `Mcp-Session-Id` echoed on every request.
- With a server of an earlier revision, an expired HTTP session (a `404` on a request
  carrying an `Mcp-Session-Id`) is handled transparently: the client re-runs `initialize` and replays the request.

</details>

**Next:** [Tools](tools.md) — the built-in tools beside these, and the approval model behind every card.
