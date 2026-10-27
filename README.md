<p align="center">
  <img src="Inferpal/assets/icon-256.png" alt="Inferpal" width="96" height="96">
</p>

<h1 align="center">Inferpal</h1>

<p align="center">
  <b>Your local AI coding agent, inside Visual Studio, VS Code and the editors you already use.</b><br>
  It reads your code, edits it behind an approval that shows the real diff, then builds and runs your tests.<br>
  The model runs on your own hardware.
</p>

<p align="center">
  No account · No telemetry · No API key · Works offline · Free and open source
</p>

<p align="center">
  <a href="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml"><img src="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml/badge.svg?branch=master" alt="CI"></a>
  <a href="https://github.com/EstaxNet/Inferpal/releases/latest"><img src="https://img.shields.io/github/v/release/EstaxNet/Inferpal" alt="Release"></a>
  <a href="https://www.gnu.org/licenses/gpl-3.0"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"></a>
  <img src="https://img.shields.io/badge/tests-7464%20passing-brightgreen" alt="Tests">
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs"><img src="https://img.shields.io/badge/Visual%20Studio-install-5C2D91" alt="Install for Visual Studio"></a>
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode"><img src="https://img.shields.io/badge/VS%20Code-install-007ACC" alt="Install for VS Code"></a>
</p>

<p align="center">
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs"><b>Visual Studio 2026 / 2022</b></a> ·
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode"><b>VS Code</b></a> (Windows, Linux, macOS) ·
  <a href="docs/acp.md"><b>Zed, JetBrains, Qt Creator, Neovim, Emacs</b></a>
</p>

<p align="center">
  <img src="docs/assets/demo-vscode.gif" alt="Inferpal in VS Code: asked to add argument validation to a C# method and cover it with xUnit tests, the agent plans, edits the method, creates the test file and runs the tests behind approval cards, and ends on its answer and a result bar with Undo run, on a local model" width="820">
  <br>
  <sub>A real run on a local model (Devstral Small 2 in LM Studio), sped up. Every write and command was approved in the chat.</sub>
</p>

---

## One turn, from question to green build

Ask in plain words, for example *"add argument validation to this method and cover it with tests"*. The agent plans,
reads, edits, builds and tests, and shows every step live. Nothing touches your disk until you have seen the change.

```mermaid
flowchart LR
    ASK(["You ask"]) --> WORK["Plan, read and<br/>search your code"]
    WORK --> EDIT["Propose<br/>a change"]
    EDIT --> YOU{"You see<br/>the real diff"}
    YOU -- Enter --> WRITE["Back up,<br/>then write"]
    YOU -. Esc .-> WORK
    WRITE --> CHECK["Build<br/>and test"]
    CHECK -. errors .-> EDIT
    CHECK --> DONE(["Answer +<br/>Undo run"])
```

<img src="docs/assets/result-bar.png" alt="The end of a run: the answer, then a result bar listing OrderServiceTests.cs +87 and OrderService.cs +8, build passed, and an Undo run button" width="380" align="right">

- **Every write, delete and command waits for you**, with the real diff. Enter allows, Esc denies — or pick a narrow
  rule such as *commands that start with `dotnet test`* (never a chain, a launcher or a program that deletes).
- **Every file is backed up before it changes.** **Undo run** puts the whole run back in one click.
- **Steer it while it works.** Type a correction and press Enter: the agent reads it before its next step.
- **It checks its own work.** A build or typecheck after each edit (.NET, TypeScript, Rust, Go) feeds the errors
  straight back.
- **Invented packages are refused before you are asked.** Local models make up package names; each one is looked up
  on nuget.org, npm or PyPI first.

<br clear="right">

## Right-click, and it's on it

| Where you are | Visual Studio | VS Code |
|---|---|---|
| **Errors** | Error List → *Fix with Inferpal*, or *Fix with AI* on a failed build | Problems or light bulb → *Fix with Inferpal* |
| **A failing test** | `/tdd` | Test view → *Make This Test Pass* · *Explain the Failure* |
| **Debugger, stopped** | *Analyze with Inferpal* (Call Stack, Locals, Watch…) | *Analyze with Inferpal* |
| **A file or a project** | Solution Explorer → *Explain* · *Review Changes* · *Attach to Chat* | Explorer → the same three |
| **A selection** | *Edit with AI…* (Ctrl+Shift+I), accepted part by part in the editor | *Edit with AI*, shown in Refactor Preview first |
| **Committing** | `/commit` drafts the message | Source Control → *Propose a Commit Message* |
| **Typing** | Ghost-text completions: Tab, or Alt+' to ask | the same |

## A failing test? It reads the debugger, not just the log

<p align="center">
  <img src="docs/assets/demo-tdd-vscode.gif" alt="/tdd in VS Code on a local Qwen3.8 27B: the failing test is run again under the debugger, the fix is shown as a diff and approved, and the suite turns green after two rounds" width="820">
</p>

`/tdd` runs your tests (dotnet, pytest, npm, cargo, go), patches the code and runs them again, up to five rounds. On
the first red round a failing .NET test runs again **under the editor's debugger**, and the real exception, call
stack and local values go into the fix.

## Measured, not promised

| On a fixed bench, with local models | Result |
|---|---|
| Real coding tasks in C#, JavaScript and Python, counted only when the project's own tests pass — Qwen3.8 27B on one 24 GB card | **14 / 14** |
| 12 failing C# tests handed to `/tdd`, local 27B model | **12 fixed** with the debugger's view, 10 without |
| A repository rule followed over 10 questions, two local models | **8 and 10** with its instruction file, **0** without |

Every figure, model by model: **[Models](docs/models.md)**. `/bench` measures the ones you have, on your machine.

## Pick a model for your card

| Your card | Agent | Autocomplete beside it |
|---|---|---|
| 24 GB | Qwen3.8 27B (14/14), which also autocompletes, or Gemma 4 12B with Mellum2 | see the agent column |
| 16 GB | Gemma 4 12B (12/14, fast), or Bonsai 27B, a 1-bit Qwen (13/14, about 3× slower) | Mellum2 fits only beside a weaker agent |
| 12 GB | same as 16 GB | none measured fits |
| 8 GB | Granite 4.2 8B (14/14, fast) | none measured fits |
| More than 24 GB | not tested | — |

Measured on one 24 GB card (LM Studio, 32K context), its free memory cut to stand in for the smaller ones. Bigger
cards, multi-GPU machines and 70B-class models were out of our budget, not out of interest: a bigger model makes a
better agent. Any model that calls tools works — [details](docs/models.md#by-graphics-card).

## One engine, every editor

```mermaid
flowchart TB
    VS["Visual Studio<br/>2026 / 2022"]
    VSC["VS Code<br/>Windows · Linux · macOS"]
    ACP["Zed · JetBrains · Qt Creator<br/>Neovim · Emacs (ACP)"]
    ENGINE["<b>Inferpal engine</b><br/>agent loop · 30 built-in tools + MCP<br/>approvals · backups · code index"]
    MODEL[("Your model server<br/>Ollama · LM Studio · llama.cpp · vLLM<br/>on this computer or your own GPU machine")]
    VS & VSC & ACP --> ENGINE
    ENGINE <--> MODEL
```

The same chat, approvals and settings in every editor — and Visual Studio and VS Code share conversations, so you can
start in one and finish in the other:

<table>
  <tr>
    <td align="center" valign="top"><img src="docs/assets/chat-visual-studio.png" alt="The Inferpal chat in Visual Studio: the agent's plan, then an approval card showing the diff of OrderService.cs with Allow once, Always this session and Deny" width="400"><br><sub>Visual Studio</sub></td>
    <td align="center" valign="top"><img src="docs/assets/chat-vscode.png" alt="The Inferpal chat in VS Code at the same moment of the same task: two steps done, then the same approval card for OrderService.cs" width="400"><br><sub>VS Code</sub></td>
  </tr>
</table>

## Get started

1. **Install** from the Marketplace — [Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs)
   or [VS Code](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode) — or from the
   [latest release](https://github.com/EstaxNet/Inferpal/releases/latest), which also carries the
   [ACP agent](docs/acp.md) for the other editors.
2. **Run a model server.** [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai), on this computer or a
   machine you own ([remote setup](docs/remote-inference.md)). On a 24 GB card: `ollama pull qwen3.8`.
3. **Open Inferpal** — *Tools → Inferpal → Inferpal Chat* in Visual Studio, the activity-bar icon in VS Code. The
   first time, it looks for your model server and sets you up with a model; `/setup` looks again.

Full walkthrough: **[Getting Started](docs/getting-started.md)**. Coming from Continue or Roo Code? `/import continue`
or `/import roo` brings your models, rules, prompts, Roo's modes and MCP servers over — [how](docs/coming-from-continue.md).

## Also in the box

- **30 built-in tools** — files, text and semantic search, build and tests, a shell with background jobs, git, the
  editor, the debugger, compiler-backed analysis and rename, web search — plus any **MCP** server and your own shell
  commands. [Reference](docs/tools.md)
- **Reviews and git text** — `/check` reviews your diff, a branch or a commit, every finding anchored to a line;
  `/commit` and `/pr-description` write the text.
- **Typed context** — `@file` `@folder` `@code` `@diff` `@commit` `@problems` `@debugger` `@clipboard` `@tree` `@url`, `@terminal` (VS Code), and
  **@Docs** to index a documentation site.
- **Your repository's setup, read as it is** — `AGENTS.md`, `CLAUDE.md`, the Copilot, Cursor, Cline, Roo and Continue
  instruction files, and `.aiignore`-style files, honoured by everything that reads for the model.
- **Modes, yours or your repository's** — beside Chat, Agent and Plan, pick a mode found for the workspace: your own,
  GitHub Copilot's custom agents or Roo Code's custom modes, read as they are. It brings its instructions, model, tools
  and effort, and adds no permission. [Modes](docs/configuration.md#modes)
- **Work beside you** — `/task` investigates in the background while you code; `/debug` drives a real debug session
  once you approve it.
- **See what the model sees** — a gauge shows how full its window is, and `/xray` what fills it, layer by layer.
- **Many models, one GPU** — `/bench` scores your installed models per role, `/arena` runs blind duels, and small
  chores (titles, commit messages, summaries) can go to a small utility model.
- **50+ slash commands** and an interface in 10 languages. [All commands](docs/slash-commands.md) ·
  [Full feature tour](docs/features.md)

## What leaves your machine

With a model server on your machine, your code does not. There is no account, no telemetry and no hosted service.
Only these go out: the model server address you choose, the MCP servers you add, a documentation site you index, the
agent's web searches and page fetches (each one asks you first), and the name of a package the agent wants to add,
looked up on its public registry — one setting turns that off.

---

## Documentation

| Use it | Set it up | Under the hood |
|---|---|---|
| [Getting Started](docs/getting-started.md) · [Features](docs/features.md) · [Slash Commands](docs/slash-commands.md) · [Mentions](docs/mentions.md) · [Tools](docs/tools.md) | [Providers](docs/providers.md) · [Models](docs/models.md) · [Configuration](docs/configuration.md) · [Remote Inference](docs/remote-inference.md) · [Other editors (ACP)](docs/acp.md) · [Coming from Continue or Roo](docs/coming-from-continue.md) | [Architecture](docs/architecture.md) · [Search & Indexing](docs/search-and-indexing.md) · [MCP](docs/mcp.md) · [Rules & Checks](docs/rules-and-checks.md) · [Development](docs/development.md) |

## Official sources

Inferpal is an independent open-source project by EstaxNet. It has no hosted version and no account, and is not
affiliated with any cloud or API provider. The only official sources are this repository's
[releases](https://github.com/EstaxNet/Inferpal/releases) and the Marketplace listings for
[Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs) and
[VS Code](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode). Since October 2026 Google
returns no result for *Inferpal*, while Bing, DuckDuckGo and Brave still do: if a search brought you nothing, these
are the links.

## Contributing

Contributions are welcome — see **[Development](docs/development.md)** for the build, the project layout, and how to
add a tool or a language. Quick version: implement `ITool`, register it in `ToolRegistry.cs`, and add any new strings
to all 10 `.resx` files **and** to `Strings.cs`.

## License

[GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0). Developed with the assistance of **Claude** (Anthropic).
