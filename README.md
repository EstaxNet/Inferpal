<p align="center">
  <img src="Inferpal/assets/icon-256.png" alt="Inferpal" width="96" height="96">
</p>

<h1 align="center">Inferpal</h1>

<p align="center">
  <b>A coding agent for Visual Studio and VS Code that runs on your own hardware.</b><br>
  It reads your code, proposes each change on the real diff, then builds and runs your tests.
</p>

<p align="center">
  No account · No telemetry · No API key · Works offline · Free and open source
</p>

<p align="center">
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs"><img src="https://img.shields.io/badge/Visual%20Studio-install-5C2D91" alt="Install for Visual Studio"></a>
  <a href="https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode"><img src="https://img.shields.io/badge/VS%20Code-install-007ACC" alt="Install for VS Code"></a>
  <a href="https://open-vsx.org/extension/EstaxNet/inferpal-vscode"><img src="https://img.shields.io/badge/Open%20VSX-install-C160EF" alt="Install from Open VSX"></a>
  <a href="https://github.com/EstaxNet/Inferpal/releases/latest"><img src="https://img.shields.io/github/v/release/EstaxNet/Inferpal" alt="Release"></a>
  <a href="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml"><img src="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml/badge.svg?branch=master" alt="CI"></a>
  <img src="https://img.shields.io/badge/tests-9327%20passing-brightgreen" alt="Tests">
</p>

<p align="center">
  <img src="docs/assets/demo-vscode.gif" alt="Inferpal in VS Code: asked to add argument validation to a C# method and cover it with xUnit tests, the agent plans, edits the method, creates the test file and runs the tests behind approval cards, and ends on its answer and a result bar with Undo run, on a local model" width="820">
  <br>
  <sub>"Add argument validation to this method and cover it with tests" — a real run on a local model (Devstral Small 2 in LM Studio), sped up.</sub>
</p>

## See it work

<p align="center">
  <img src="docs/assets/demo-tdd-vscode.gif" alt="/tdd in VS Code on a local Qwen3.8 27B: the failing test is run again under the debugger, the fix is shown as a diff and approved, and the suite turns green after two rounds" width="820">
  <br>
  <sub><b>A failing test, fixed with the debugger's view.</b> <code>/tdd</code> runs it again under the debugger and fixes from the real exception and local values.</sub>
</p>

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/demo-viewpage-dark.gif">
    <img src="docs/assets/demo-viewpage-light.gif" width="754" alt="The chat in VS Code: asked why customers cannot find the Pay button on a phone, the agent looks at the checkout page at 390 px, edits site.css, and looks again. Beside it, the two pictures it looked at: before, the order summary runs past the right edge and the Pay button is off the screen; after the fix, the prices and the Pay button show">
  </picture>
  <br>
  <sub><b>It looks at your app.</b> <code>view_page</code> opens the page at a phone's width, sees the Pay button off the screen, fixes the CSS and looks again.<br>Replayed step by step in the chat panel, beside the two pictures Inferpal took of the page.</sub>
</p>

<table>
  <tr>
    <td align="center" valign="top" width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/assets/features/documents-vs-dark.png">
        <img src="docs/assets/features/documents-vs-light.png" alt="Visual Studio: a question with a PDF spec and an Excel workbook attached: the answer quotes page 7 of the spec and two rows of the workbook, in a table" width="400">
      </picture>
      <br><sub><b>Your spec is a PDF, your cases are in Excel.</b> Read on your machine, quoted page by page and row by row.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/assets/features/diagram-vscode-dark.png">
        <img src="docs/assets/features/diagram-vscode-light.png" alt="VS Code: a state diagram of an order drawn in the chat, under a bar with Diagram, Code, copy and Open larger" width="400">
      </picture>
      <br><sub><b>Ask for a diagram, get it drawn.</b> Its Mermaid code one click away.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/assets/features/editor-vscode-dark.png">
        <img src="docs/assets/features/editor-vscode-light.png" alt="VS Code: a finished run with its result bar, then the next request waiting on an approval card that shows the new test file as a diff, with Allow once, Always this session and Deny" width="400">
      </picture>
      <br><sub><b>Nothing changes without you.</b> Every write and command waits on the real diff: Enter allows, Esc refuses.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/assets/features/review-run-dark.png">
        <img src="docs/assets/features/review-run-light.png" alt="VS Code: the changes of a run, file by file, each with Keep and Undo; a file changed after the run is left as it is and said" width="400">
      </picture>
      <br><sub><b>Undo the whole run, or each change on its own.</b> Every file is backed up before it is written.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><img src="docs/assets/chat-visual-studio.png" alt="The Inferpal chat in Visual Studio: the agent's plan, then an approval card showing the diff of OrderService.cs with Allow once, Always this session and Deny" width="400"><br><sub><b>Visual Studio 2026 / 2022</b></sub></td>
    <td align="center" valign="top" width="50%"><img src="docs/assets/chat-vscode.png" alt="The Inferpal chat in VS Code at the same moment of the same task: two steps done, then the same approval card for OrderService.cs" width="400"><br><sub><b>VS Code</b> on Windows, Linux and macOS — conversations go from one editor to the other</sub></td>
  </tr>
</table>

It also opens your web app at phone width, looks at the window of a desktop app it started, calls your service, reads
logs, notebooks and archives, and works from a right-click on an error, a failing test or a selection. Zed, JetBrains
IDEs, Qt Creator, Neovim and Emacs get the same agent through the [Agent Client Protocol](docs/acp.md).

## Your code stays with you

The model runs on a server you choose — Ollama, LM Studio, llama.cpp, vLLM — on this computer or a GPU machine you own
([remote setup](docs/remote-inference.md)). No account, no telemetry, no hosted service. The only other things that go
out are the ones you set up or approve: the MCP servers you add, a documentation site you index, a web search or a page
the agent asks to read, and the name of a package it wants to add, checked on its public registry (one setting turns
that off).

## Which model for your graphics card

| Your card | Agent model | Real coding tasks solved, tests passing |
|---|---|---|
| 24 GB | Qwen3.8 27B | 14 / 14 |
| 12 – 16 GB | Gemma 4 12B, fast · Ternary Bonsai 2 27B, about 4× slower | 12 / 14 · 14 / 14 |
| 8 GB | Granite 4.2 8B | 14 / 14 |

Measured on our own bench. Any model that calls tools works. [All measurements](docs/models.md)

## Get started

1. **Install** for [Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs) or
   [VS Code](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode)
   ([Open VSX](https://open-vsx.org/extension/EstaxNet/inferpal-vscode) for VSCodium and Cursor).
2. **Run a model server** — [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai). On a 24 GB card:
   `ollama pull qwen3.8`.
3. **Open Inferpal** — *Tools → Inferpal → Inferpal Chat* in Visual Studio, the activity-bar icon in VS Code. It finds
   your model server and sets you up.

[Getting Started](docs/getting-started.md) · Coming from Continue or Roo Code? `/import` brings your setup over
([how](docs/coming-from-continue.md)).

## Learn more

[Feature tour](docs/features.md) — 41 built-in tools and any MCP server, more than 50 slash commands, `/check` reviews,
commit messages, background tasks, modes, your repository's `AGENTS.md`, an interface in 10 languages.

| Use it | Set it up | Under the hood |
|---|---|---|
| [Getting Started](docs/getting-started.md) · [Features](docs/features.md) · [Slash Commands](docs/slash-commands.md) · [Mentions](docs/mentions.md) · [Tools](docs/tools.md) · [File formats](docs/file-formats.md) | [Providers](docs/providers.md) · [Models](docs/models.md) · [Configuration](docs/configuration.md) · [Remote Inference](docs/remote-inference.md) · [Other editors (ACP)](docs/acp.md) · [Coming from Continue or Roo](docs/coming-from-continue.md) | [Architecture](docs/architecture.md) · [Search & Indexing](docs/search-and-indexing.md) · [MCP](docs/mcp.md) · [Rules & Checks](docs/rules-and-checks.md) · [Development](docs/development.md) |

Website: [inferpal.estaxnet.fr](https://inferpal.estaxnet.fr/) · Official sources: this repository's
[releases](https://github.com/EstaxNet/Inferpal/releases) and the
[Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs),
[VS Code](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode) and
[Open VSX](https://open-vsx.org/extension/EstaxNet/inferpal-vscode) listings. Inferpal is an independent project by
EstaxNet, with no hosted version.

Contributions are welcome — see [Development](docs/development.md).
[GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0) · Developed with the assistance of **Claude** (Anthropic).
