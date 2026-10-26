# Your local AI agent, inside VS Code's own views

**Right-click a failing test, a problem or a file. Inferpal reads your code, edits it behind an approval that shows the real diff, then builds and runs your tests until they pass or it runs out of rounds. The model runs on your own hardware.**

**No account · No telemetry · No API key · Works offline · Free and open source (GPL v3)**

Windows, Linux and Apple Silicon, with the backend bundled (no .NET to install) · [Ollama](https://ollama.com), [LM Studio](https://lmstudio.ai), llama.cpp, vLLM or any OpenAI-compatible server, on this computer or a GPU machine you own · also for [Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs)

![/tdd in VS Code on a local Qwen3.8 27B: the failing test is run again under the debugger, the fix is shown as a diff and approved, and the suite turns green after two rounds](https://raw.githubusercontent.com/EstaxNet/Inferpal/ad9e01d25a5446bc151a78ded21da12f72d6f0a4/docs/assets/demo-tdd-vscode.gif)

*A real run on a local model, sped up: `/tdd` reads the debugger, proposes the fix, and the suite turns green.*

---

## Right-click, and it's on it

| Where you are | Choose | What you get |
|---|---|---|
| **Test view** | Make This Test Pass · Explain the Failure | `/tdd` on that test, or its failure explained |
| **Problems, light bulb** | Fix with Inferpal | The problem is fixed with the code around it |
| **Debugger, stopped** | Analyze with Inferpal | The stop is explained from the stack and locals, and nothing is stepped or run |
| **Explorer** | Explain · Review Changes · Attach to Chat | A file or folder explained, or its uncommitted changes reviewed |
| **Source Control** | Propose a Commit Message | The message is written into the commit box, and nothing is committed |
| **Editor** | Edit with AI | Your instruction applied to the selection, shown in Refactor Preview first |
| **While you type** | Tab, or Alt+' | Ghost-text completions, either after a pause or only when you ask |

Open the chat from the **Inferpal** icon in the activity bar. Every change waits for your approval.

## A failing test? It reads the debugger, not just the log

`/tdd` runs your tests (dotnet, pytest, npm, cargo, go), patches the code and runs them again, for up to five rounds. On the first red round, a failing .NET test is run again under the debugger, with no `launch.json` needed. The real exception, call stack and local values then go into the fix. We tested this on a fixed set of 12 failing C# tests with a local 27B model. It fixed **12 with the debugger's view and 10 without**.

## Hand it a task. You stay in control.

Ask in plain words, for example *"add argument validation to this method and cover it with tests"*. The agent plans the work, then reads, edits, builds and tests, and shows every step live.

![Inferpal in VS Code: asked to add argument validation to a C# method and cover it with xUnit tests, the agent plans, edits the method, creates the test file and runs the tests behind approval cards, and ends on its answer and a result bar with Undo run, on a local model](https://raw.githubusercontent.com/EstaxNet/Inferpal/211c21968b4ec213ff44845645c702123643e37b/docs/assets/demo-vscode.gif)

*A real run, sped up. Every write and command was approved in the chat, and the whole run can be undone in one click.*

- **Every write, delete and command waits for you**, with the real diff. Press Enter to allow or Esc to deny. You can also allow a narrow rule you choose, such as *commands that start with `npm test`*.
- **Every file the agent edits is backed up first.** **Undo run** puts them all back in one click.
- **You can steer it while it works.** Type a correction and press Enter, and the agent reads it before its next step. Ctrl+Enter queues a message for after the turn instead.
- **It checks its own work.** It builds or typechecks after each edit (.NET, TypeScript, Rust, Go) and feeds the errors straight back.
- **It refuses invented packages before asking you.** Local models make up package names. Before a package is added, its name is looked up on nuget.org, npm or PyPI.
- **Long runs announce themselves.** If a run takes more than 30 seconds while you look elsewhere, a notification says when it is done.

## Bigger hardware, better agent

Inferpal runs whatever model your server holds, and a bigger model makes it better. On our test server, which has one 24 GB card, the largest model it could hold scored best: **Qwen3.8 27B completed 14 of 14 real coding tasks** in C#, JavaScript and Python. Each task counted only if the project's own tests passed. More memory also gives you a longer context, and room for a separate autocomplete model beside the agent.

| Your card | Agent | Autocomplete beside it |
|---|---|---|
| 24 GB | Qwen3.8 27B (14/14), which also autocompletes, or Gemma 4 12B with Mellum2 | see the agent column |
| 16 GB | Gemma 4 12B (12/14, fast), or Bonsai 27B, a 1-bit Qwen (13/14, about 3× slower) | Mellum2 fits only beside a weaker agent |
| 12 GB | same as 16 GB | none measured fits |
| 8 GB | Granite 4.2 8B (14/14, fast) | none measured fits |
| More than 24 GB | not tested | — |

These were measured on one 24 GB card (LM Studio, 32K context), with its free memory cut to stand in for the smaller cards. Each model ran once, so a gap of a task or two means nothing. We could not test larger cards, multi-GPU machines, 70B-class models or a Mac with lots of memory, for lack of budget and infrastructure rather than interest, so we recommend nothing above 24 GB. `/bench` measures the models you have, on your machine. See [every figure](https://github.com/EstaxNet/Inferpal/blob/master/docs/models.md).

---

## Get started

1. **Run a model server.** [Ollama](https://ollama.com) and [LM Studio](https://lmstudio.ai) are both free, and can run on this computer or on a machine you own. On a 24 GB card: `ollama pull qwen3.8`.
2. **Open your project folder** in a trusted workspace.
3. **Open Inferpal** from its activity-bar icon. The first time, it looks for a model server on this computer and proposes the models that fit your card: **Start with these**. For a server on another machine, type its address on the same card. On Ollama without a chat model, the card downloads one for you. `/setup` brings the card back.
4. **Ask a question, or right-click a failing test.**

**Coming from Continue or Roo Code?** `/import continue` or `/import roo` brings over your model server, models, rules, prompts, MCP servers and documentation sites. A card shows what it will take, and nothing is written until you apply. See [Coming from Continue or Roo Code](https://github.com/EstaxNet/Inferpal/blob/master/docs/coming-from-continue.md).

## Also in the box

- **30 built-in tools**: files, text and semantic search, build and tests, shell with background jobs, git, the editor, the debugger, compiler-backed analysis and symbol rename, web search and page fetch. Add any **MCP** server's tools and your own shell commands. See the [reference](https://github.com/EstaxNet/Inferpal/blob/master/docs/tools.md).
- **Semantic code search** across your workspace. The most relevant code is added to each question, and suggested as chips while you type.
- **Reviews and Git text.** `/check` reviews your diff, a branch or a commit, and anchors every finding to a line. `/commit` drafts the commit message, and `/pr-description` writes the pull request description.
- **Typed mentions**: `@file` `@folder` `@code` `@diff` `@problems` `@debugger` `@clipboard` `@tree`. **@Docs** indexes a documentation site with `/docs add <url>`.
- **Your repository's agent setup, read as it is.** It reads `AGENTS.md`, `CLAUDE.md`, the Copilot and Cursor instruction files, and your project rules. It honours `.aiignore`-style files.
- **Background and debug sessions.** `/task` investigates in the background while you work. `/debug` drives a debug session once you approve it.
- **Context you can see.** A gauge shows how full the model's window is, and `/xray` shows what fills it.
- **One engine, every editor.** The same agent runs in [Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs), with shared settings and conversations, so you can start in one editor and finish in the other. It is also an [Agent Client Protocol](https://github.com/EstaxNet/Inferpal/blob/master/docs/acp.md) agent for Zed, the JetBrains IDEs, Qt Creator, Neovim and Emacs.
- **50+ slash commands** and an interface in 10 languages. See the [slash commands](https://github.com/EstaxNet/Inferpal/blob/master/docs/slash-commands.md).

---

## FAQ

**Does my code leave my machine?** Not with a model server on your machine. There is no account, no telemetry and no hosted service. Only these go out:

- the model server address you choose
- the MCP servers you add
- a site you index
- the agent's web searches and page fetches, each of which asks you first
- the name of a package the agent wants to add, looked up on its public registry. Only the name is sent, and one setting turns this off.

**Will my model work?** Any model that calls tools will. That includes tool calls a server leaves as plain text (Qwen, Mistral, GLM, Gemma, gpt-oss). It also includes a model whose chat template refuses tools. Each known model family gets the sampling settings its vendor recommends.

**Which server should I use?** Ollama gets every feature, including per-model memory figures, downloads from the chat and unloading idle models. LM Studio runs everything without the per-model memory figures. Other OpenAI-compatible servers (llama-server, vLLM) run the chat, the agent and the search. They also run inline completions if the server can complete around the cursor.

**Can the model run on another machine?** Yes. Point the server address at it, as described in [Remote inference](https://github.com/EstaxNet/Inferpal/blob/master/docs/remote-inference.md).

**Does it work with Remote - WSL, Remote - SSH or Dev Containers?** Yes: Inferpal runs on the other side, next to your files. There, `localhost` is WSL, the remote machine or the container — Inferpal says so when a local address does not answer, with the address to use instead ([details](https://github.com/EstaxNet/Inferpal/blob/master/docs/remote-inference.md#5-vs-code-connected-to-wsl-ssh-or-a-container)).

**Does it work offline?** Yes, with a local server. Only web search and page fetch need the internet.

## Requirements

| | |
|---|---|
| VS Code | 1.100 or later, with a folder open in a trusted workspace |
| Platforms | Windows x64, Linux x64 and Apple Silicon (macOS 12 or later). Each build ships its own backend, and all are on the [GitHub releases](https://github.com/EstaxNet/Inferpal/releases) page. |
| Model server | Ollama, LM Studio or any OpenAI-compatible server, local or on another machine, with a model that calls tools |

## Official sources

Inferpal is an independent open-source project (GPL v3) by EstaxNet. It has no hosted version and no account, and is not affiliated with any cloud or API provider. The official sources are the [GitHub releases](https://github.com/EstaxNet/Inferpal/releases) and the Marketplace listings for [Visual Studio](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs) and [VS Code](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode). Questions and bug reports go on [GitHub](https://github.com/EstaxNet/Inferpal/issues).

**Finding Inferpal useful?** [A rating](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode&ssr=false#review-details) helps other developers find it.

## License

GPL-3.0. The source is at [github.com/EstaxNet/Inferpal](https://github.com/EstaxNet/Inferpal).
