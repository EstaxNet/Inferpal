# Inferpal in Zed, JetBrains IDEs, Qt Creator, Neovim and Emacs

Inferpal speaks the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), the protocol editors use to run a
coding agent the way they run a language server. The same engine as the Visual Studio and VS Code extensions —
the agent, the tools, the approvals, semantic search, MCP — runs in any ACP client: **Zed**, **JetBrains IDEs**
(Rider, IntelliJ IDEA, PyCharm, …), **Qt Creator**, **Neovim** (CodeCompanion, avante.nvim), **Emacs** (agent-shell),
and Visual Studio through **Code Wicket**.

It runs against **your** model server — Ollama, LM Studio or any OpenAI-compatible endpoint. No account, no telemetry.

## Install

1. Download the archive for your system from the [latest release](https://github.com/EstaxNet/Inferpal/releases/latest):

   | System | Archive | Executable inside |
   |---|---|---|
   | Windows x64 | `inferpal-acp-win32-x64-<version>.zip` | `Inferpal.Host.exe` |
   | Linux x64 | `inferpal-acp-linux-x64-<version>.tar.gz` | `Inferpal.Host` |
   | macOS (Apple Silicon) | `inferpal-acp-darwin-arm64-<version>.tar.gz` | `Inferpal.Host` |

   Check it against `SHA256SUMS.txt` from the same release, then extract it anywhere. Nothing else is needed: the
   archive carries its own .NET 10 runtime, which asks for macOS 12 or later, or a Linux with glibc 2.27 or later.
2. Tell your editor to run that executable with the `--acp` argument (below).

On Linux, a system without `libicu` runs Inferpal in a reduced globalization mode; install `libicu` (or `icu-libs`)
for full locale support.

### Zed

Agent Panel → **External Agents** → **Add Agent** → **Add Custom Agent**, then in `settings.json`:

```json
{
  "agent_servers": {
    "Inferpal": {
      "type": "custom",
      "command": "/path/to/Inferpal.Host",
      "args": ["--acp"],
      "env": {}
    }
  }
}
```

Start a thread with **Inferpal** from the Agent Panel's new-thread menu.

### JetBrains IDEs (Rider and the others)

The AI Chat needs the **AI Assistant** plugin: the AI Chat panel offers to install it, then restart the IDE. AI
Chat → **⋯** → **Add Custom Agent** opens `~/.jetbrains/acp.json`:

```json
{
  "agent_servers": {
    "Inferpal": {
      "command": "C:\\Tools\\Inferpal\\Inferpal.Host.exe",
      "args": ["--acp"]
    }
  }
}
```

Give the **full path** of the executable. No JetBrains AI subscription is needed, and the JetBrains AI activation
the panel asks for can be left incomplete. Pick **Inferpal** in the AI Chat agent list.

Seen with Rider 2026.2: answers with their thinking, the mode and model buttons, every change asked first — **click
the file name on the card** to open its diff before you answer —, Stop, and the threads listed in **Chat History** and
reopened after Rider was closed and started again (a Rider ended by force came back with an empty list).

- ⚠ **Rider hands every session its own MCP server**: 93 tools, about 40,000 tokens of tool definitions sent with
  every question — more than a model loaded with a 32K context can take. Inferpal's message then names that share.
  Turn off the tool groups you do not need in **Settings → Tools → MCP Server → Exposed Tools** (each group off comes
  off every question: dotTrace's six tools are about 4,400 tokens), or load the model with a larger context.
- ⚠ **When one call changes several files, Rider's card names the first one only, and opens only its diff.** The
  other files are in the same approval: Inferpal sends every file's change with the question, but Rider does not show
  them. To see each change before it is made, ask for one file at a time.
- Rider attaches the file you are viewing to every message.
- Inferpal reads files through Rider, as Rider holds them: a file another program has just changed can be read in the
  version Rider loaded last, until Rider reloads it.

### Neovim (CodeCompanion)

```lua
require("codecompanion").setup({
  adapters = {
    acp = {
      inferpal = function()
        local helpers = require("codecompanion.adapters.acp.helpers")
        return {
          name = "inferpal",
          formatted_name = "Inferpal",
          type = "acp",
          roles = { llm = "assistant", user = "user" },
          commands = { default = { "/path/to/Inferpal.Host", "--acp" } },
          defaults = { mcpServers = {}, timeout = 60000 },
          parameters = {
            protocolVersion = 1,
            clientCapabilities = { fs = { readTextFile = true, writeTextFile = true } },
            clientInfo = { name = "CodeCompanion.nvim", version = "1.0.0" },
          },
          handlers = {
            setup = function(self) return true end,
            auth = function(self) return true end,
            form_messages = function(self, messages, capabilities)
              return helpers.form_messages(self, messages, capabilities)
            end,
            on_exit = function(self, code) end,
          },
        }
      end,
    },
  },
  interactions = { chat = { adapter = "inferpal" } },
})
```

A local model can take a while to load on the first question: keep `timeout` generous.

### Qt Creator

Qt Creator 21 has an ACP client built in. **Preferences → AI → ACP Servers → Add**: a name, the **Executable** (full
path of `Inferpal.Host`) and **Arguments** `--acp`. Open the chat with the AI chat button of the editor's toolbar (or
**Tools → ACP Client → Show Agentic AI Chat in Side Panel**) and pick **Inferpal**.

Seen with the Qt Creator 21 beta: answers with their thinking, the context gauge, the mode and model buttons, every
change asked first with its diff on the card, Stop, and the threads listed and reopened after a restart.

- ⚠ **Qt Creator hands every session its own MCP server**: 159 tools, about 30,000 tokens of tool definitions sent
  with every question — more than a model loaded with a 32K context can take, before the question itself. Inferpal's
  message then names that share. Turn off the tool groups you do not need in **Preferences → AI → Qt Creator MCP
  Server** (that choice is kept; in the beta, the server's own *Enable* switch is back on at every start), or load
  the model with a larger context.
- It sends the Qt documentation MCP server too, which is reached over the Internet.
- Set Inferpal up before the first session (`Inferpal.Host --acp --setup` in a terminal, or the settings of Visual
  Studio or VS Code).

### Visual Studio, beside other agents (Code Wicket)

Inferpal's own Visual Studio extension is the way to use it in Visual Studio. To run it among other agents in the
[Code Wicket](https://github.com/MattDarg/code-wicket) chat, add it under **Settings → Backends → Custom ACP agents**:

```json
[
  {
    "providerId": "inferpal",
    "displayName": "Inferpal",
    "cliPath": "C:\\Tools\\Inferpal\\Inferpal.Host.exe",
    "args": ["--acp"]
  }
]
```

Open **Extensions → Code Wicket → Code Wicket Chat** and pick **Inferpal** in the agent list.

Seen with Code Wicket 1.0.0 in Visual Studio 2022: answers with their thinking, the model list, the context ring,
every change asked first (the card shows the call's arguments — for an edit, the old and the new text — not a diff),
and Stop.

- Code Wicket hands Inferpal its own MCP server: 17 tools (build, diagnostics, tests, debugger), about 9,000 tokens.
- With a folder opened instead of a solution, Code Wicket started the session in the folder **above** it, and
  Inferpal worked on that whole folder. Not tried with a solution open.

### acp-ui (desktop)

[acp-ui](https://github.com/formulahendry/acp-ui) reads its agents from `agents.json` (`%APPDATA%\acp-ui\` on
Windows, `~/.config/acp-ui/` on Linux, `~/Library/Application Support/acp-ui/` on macOS):

```json
{
  "agents": {
    "Inferpal": {
      "command": "C:\\Tools\\Inferpal\\Inferpal.Host.exe",
      "args": ["--acp"],
      "env": {}
    }
  }
}
```

Pick **Inferpal**, choose the folder with the folder button (the field does not take a typed path on the desktop),
then **New Session**.

Seen with acp-ui 0.1.16 on Windows: answers with their thinking, the mode button, every change asked first, Stop, and
the saved sessions reopened after a restart.

- ⚠ **acp-ui's approval dialog shows the call's title (*Change a file · alpha.txt*) and its kind, not the diff**, and
  neither does the call's line afterwards: the change is seen in the file once it is made.
- A reopened session has no mode button until a new one is started.
- Its phone and browser builds reach an agent through a WebSocket bridge: read
  [Running the agent across machines](acp-security.md) first — the bridge acp-ui shows listens on every interface.

### Emacs and other clients

Any ACP client runs Inferpal the same way: the executable, with `--acp`, as a local agent over stdio. Two that we
have not played, read in their code and documentation:

- **Toad** (terminal, Linux and macOS — WSL on Windows): its agent list is the one it ships, and
  `toad acp "/path/to/Inferpal.Host --acp" /path/to/project -t Inferpal` starts any agent.
- **hapi** (phone and browser) offers a fixed list of agents; its DeepSeek Harness entry takes any ACP server —
  `HAPI_DSH_ACP_COMMAND=/path/to/Inferpal.Host HAPI_DSH_ACP_ARGS_JSON='["--acp"]' hapi dsh` —, for fresh sessions only.

## Set it up

Inferpal uses the settings Visual Studio and VS Code already share (`%AppData%\Inferpal\config.json` on Windows,
`~/.config/Inferpal/config.json` elsewhere): if you use Inferpal in one of them, there is nothing to do.

Otherwise, the first session asks you to set Inferpal up. A client that can run a command in a terminal (Zed does)
offers two buttons:

- **Open Inferpal's settings** — every setting, in your browser: the very settings window VS Code shows (the model
  server and its models, the agent and its approvals, context and memory, code search, MCP servers, language), with
  its **Test** button, the models loaded right now and the model suggestions. Save, close the page, and the session
  starts.
- **Set up Inferpal** — the short version, in the terminal: the model server, its address and a model.

Both run in any terminal too, for the folder you name (the current one by default):

```
Inferpal.Host --settings [folder]
Inferpal.Host --acp --setup
```

Once Inferpal is set up, type **`/settings`** in any thread to open the same page again. What you save there applies
from your next question, in that thread and the others — a new address or API key of the same kind of model server
included — except a new kind of server (Ollama, LM Studio, OpenAI-compatible), which a thread only uses once it is
started anew (the thread says so). A model picked in a thread stays that thread's. A settings file that cannot be
read — a hand edit that broke it — changes nothing in an open thread, which says so once.

The settings page is served by Inferpal itself, on `127.0.0.1` only, under an address nobody else can guess; it
reaches the settings and nothing else (no chat, no command, no tool; of your files, it opens only those the form
lists; it shows the code index saved for the folder and builds none — a chat in that folder keeps it up to date, or
the page's **Rebuild** button), and it stops a few seconds after its last tab is closed. A browser cannot hand a file's location to a page: type or paste a path where the form asks for one.

With a local Ollama already running, there is nothing to set up: Inferpal finds it.

## What you get

- **Three modes** — *Default* (answers, and uses the tools as needed), *Agent* (plans the task, then carries it out
  step by step), *Plan* (reads and explores, changes nothing) — and the **model**, chosen among the models your server
  has installed. Both appear as the session's options in your client. The model shown is the one that answers: in
  *Agent* mode, the model set for the agent's role when you set one; a model you pick answers in every mode. While the
  agent works through its plan, what the model writes is shown as its thinking, and the answer once, at the end.
- **Every change asks first**, with the real diff of each file shown before anything is written — the same approval
  rules, deny lists and session grants as in Visual Studio and VS Code. A file you are editing with unsaved changes is
  never written over: Inferpal asks your editor for its buffer, and refuses.
- **The tool calls on screen**: what runs, on which file, and its result; the agent's plan when it makes one.
- **Notices and compactions, where your client shows them** (ACP 1.11, Zed 1.25 and later): what Inferpal says beside
  the answer — the model it picked because the configured one is not installed, what this editor does not offer, a
  rule written to your settings, a documentation crawl's verdict — arrives as a session notice, out of the transcript;
  a conversation made to fit before a question arrives as a compaction, with the summary the model now reads. A client
  that does not declare them gets the text and the `context_compact` step as before. The end of a cut answer and a
  `/task` report always stay in the transcript.
- **Slash commands** — the client lists them (`/help` describes them all). Your repository's instructions, commands,
  skills and MCP servers work as in the other editors — a repository MCP server asks for your agreement at the first
  prompt of a new thread — except a repository MCP server that asks for a value when it starts: an ACP client gives Inferpal no box to ask it in, so that server is not started — the support bundle
  (`/diagnostics export`) names it, with the reason.
- **The MCP servers configured in your client** are started for the session, beside Inferpal's own.
- **Sessions are saved** after every turn and listed again for the folder they belong to; reopened, they come back
  whole. A session VS Code or Visual Studio saves for the same folder is listed too, branches included (from this
  version on: older ones do not record their folder).

## What stays in Visual Studio and VS Code

ACP gives an agent no editor of its own, so these are not available in an ACP client — the first answer in each
editor says so, once:

- inline suggestions (ghost text);
- the debugger tools (`/debug`, the debugger capture of `/tdd`);
- the Context X-Ray (the settings open in your browser instead: `/settings`);
- the commands that work on the editor or its panels: `/xray`, `/explain`, `/review`, `/fix`, `/refactor`, `/doc`,
  `/export`, `/branch`, `/agent-step`, `/resume`, and `/test` (it works on the editor's active file). To start over,
  open a new thread instead of `/clear`;
- a note to a running turn (Enter during a turn in Visual Studio and VS Code): the protocol has no way to send a
  message into a prompt that is still running, and a second prompt during a turn is refused. Zed queues a message typed
  during a turn and sends it when the turn ends; other clients may refuse it — stop the turn, then send the correction.

`/fix-build` and `/setup` are Visual Studio's alone: they are in the command list, as in VS Code, and answer that they
are not available here (the first setup of an ACP client is `Inferpal.Host --acp --setup`).

## On another machine

The agent is meant to run on the machine of the editor that starts it. To drive it from another computer, a phone or a
browser, read [Running Inferpal's ACP agent across machines](acp-security.md) first: whoever holds the agent's
connection answers its approvals.

## When something goes wrong

- **Zed**: Command Palette → `dev: open acp logs` shows every message, and what the agent wrote on stderr.
- **Inferpal**: `/diagnostics` lists what went wrong on its side, with the cause.
- The agent writes nothing but protocol messages on stdout; its own messages go to stderr.
