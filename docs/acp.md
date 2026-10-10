# Inferpal in Zed, JetBrains IDEs, Neovim and Emacs

Inferpal speaks the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), the protocol editors use to run a
coding agent the way they run a language server. The same engine as the Visual Studio and VS Code extensions —
the agent, the tools, the approvals, semantic search, MCP — runs in any ACP client: **Zed**, **JetBrains IDEs**
(Rider, IntelliJ IDEA, PyCharm, …), **Neovim** (CodeCompanion, avante.nvim) and **Emacs** (agent-shell).

It runs against **your** model server — Ollama, LM Studio or any OpenAI-compatible endpoint. No account, no telemetry.

## Install

> The archives ship from the first release after 1.7.5. Until then, build yours from a clone of this repository
> (.NET 8 SDK and PowerShell 7): `pwsh ./acp/package.ps1 -Target win32-x64` (or `linux-x64`, `darwin-arm64`), which
> writes the archive below into `acp/out/`.

1. Download the archive for your system from the [latest release](https://github.com/EstaxNet/Inferpal/releases/latest):

   | System | Archive | Executable inside |
   |---|---|---|
   | Windows x64 | `inferpal-acp-win32-x64-<version>.zip` | `Inferpal.Host.exe` |
   | Linux x64 | `inferpal-acp-linux-x64-<version>.tar.gz` | `Inferpal.Host` |
   | macOS (Apple Silicon) | `inferpal-acp-darwin-arm64-<version>.tar.gz` | `Inferpal.Host` |

   Check it against `SHA256SUMS.txt` from the same release, then extract it anywhere. Nothing else is needed: the
   archive carries its own .NET runtime.
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

AI Chat → **⋯** → **Add Custom Agent**, which opens `~/.jetbrains/acp.json`:

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

Give the **full path** of the executable. No JetBrains AI subscription is needed. Pick **Inferpal** in the AI Chat
agent list.

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

### Emacs and other clients

Any ACP client runs Inferpal the same way: the executable, with `--acp`, as a local agent over stdio.

## Set it up

Inferpal uses the settings Visual Studio and VS Code already share (`%AppData%\Inferpal\config.json` on Windows,
`~/.config/Inferpal/config.json` elsewhere): if you use Inferpal in one of them, there is nothing to do.

Otherwise, the first session asks you to set Inferpal up — a client that can run a setup in a terminal (Zed does)
offers a **Set up Inferpal** button. You can also run it yourself, in any terminal:

```
Inferpal.Host --acp --setup
```

It asks for the model server and its address, checks that it answers, lists its models and saves your choice. With a
local Ollama already running, there is nothing to set up: Inferpal finds it.

## What you get

- **Three modes** — *Default* (answers, and uses the tools as needed), *Agent* (plans the task, then carries it out
  step by step), *Plan* (reads and explores, changes nothing) — and the **model**, chosen among the models your server
  has installed. Both appear as the session's options in your client.
- **Every change asks first**, with the real diff of each file shown before anything is written — the same approval
  rules, deny lists and session grants as in Visual Studio and VS Code. A file you are editing with unsaved changes is
  never written over: Inferpal asks your editor for its buffer, and refuses.
- **The tool calls on screen**: what runs, on which file, and its result; the agent's plan when it makes one.
- **Slash commands** — the client lists them (`/help` describes them all). Your repository's instructions, commands,
  skills and MCP servers work as in the other editors — except a repository MCP server that asks for a value when it
  starts: an ACP client gives Inferpal no box to ask it in, so that server is not started — the support bundle
  (`/diagnostics export`) names it, with the reason.
- **The MCP servers configured in your client** are started for the session, beside Inferpal's own.
- **Sessions are saved** after every turn and listed again for the folder they belong to; reopened, they come back
  whole. A session VS Code saves for the same folder is listed too (from this version on: older ones do not record their folder).

## What stays in Visual Studio and VS Code

ACP gives an agent no editor of its own, so these are not available in an ACP client — the first answer of a session
says so once:

- inline suggestions (ghost text);
- the debugger tools (`/debug`, the debugger capture of `/tdd`);
- the Context X-Ray and the settings pages;
- the commands that work on the editor or its panels: `/xray`, `/explain`, `/review`, `/fix`, `/refactor`, `/doc`,
  `/export`, `/branch`, `/agent-step`. To start over, open a new thread instead of `/clear`.

## When something goes wrong

- **Zed**: Command Palette → `dev: open acp logs` shows every message, and what the agent wrote on stderr.
- **Inferpal**: `/diagnostics` lists what went wrong on its side, with the cause.
- The agent writes nothing but protocol messages on stdout; its own messages go to stderr.
