# Getting Started

This guide takes you from nothing to a working Inferpal chat in Visual Studio — the last
sections cover the **VS Code extension** (at feature parity since 1.2.0).

## 1. Requirements

| Requirement | Details |
|---|---|
| Editor | **Visual Studio 2026** — Community, Professional or Enterprise — or **VS Code** (Windows x64, Linux x64, or Apple Silicon). ⚠ The Visual Studio listing shows 17.14 as its floor because the Marketplace no longer accepts a 2026-only installation target; the in-editor half still needs 2026. |
| .NET SDK | .NET 8 (building from source only) |
| Model server | [Ollama](https://ollama.com) (default — full hardware-aware features), [LM Studio](https://lmstudio.ai), or any **OpenAI-compatible** server (llama.cpp, vLLM, …) |

> [!IMPORTANT]
> Tool calling is required for the agent. The models measured best at it are `qwen3.8` and
> `devstral-small-2` — see **[Models](models.md)** for every model's results. A model not trained for
> tool calling (`qwen2.5-coder`, `llama3` v1) chats but makes a poor agent.
>
> - **Inline completions** work with any model; one trained for it completes far better (Mellum2 and Qwen3.8 measured best — see [By graphics card](models.md#by-graphics-card)).
> - **Semantic search** can add a dedicated embedding model — optional: without one, the search runs on keywords. EmbeddingGemma (`embeddinggemma`) measured best — see [Models](models.md).

## 2. Start a model server

Pick whichever backend you already use — see **[Providers](providers.md)** for the
differences.

- **Ollama** (default `:11434`)
  ```powershell
  ollama serve
  ollama pull qwen3.8            # chat and agent (or devstral-small-2: faster)
  # Optional, dedicated models:
  # inline completions: the model that fits beside the agent depends on your card (docs/models.md#by-graphics-card)
  ollama pull embeddinggemma     # semantic search (optional)
  ```
- **LM Studio** (default `:1234`)
  1. Open the **Developer** tab (the server view) and **Start** the server.
  2. **Load** a tool-calling chat model (Qwen3.8 27B or Devstral Small 2) — and, optionally, an autocomplete model ([Mellum2](models.md#mellum) measured best) and
     an embedding model for semantic search.
  3. Inferpal uses LM Studio's native `/api/v1/*` API for the model list and load/unload, so
     `/models` works here too. Point the **Server URL** at `http://localhost:1234`.
- **OpenAI-compatible** — expose the server's `/v1` endpoint (and an API key if it needs one).

The backend does not have to run on the machine hosting Visual Studio — see
**[Remote Inference](remote-inference.md)**.

## 3. Build and install the extension

The quickest path is the **[Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vs)**
listing, *Inferpal for Visual Studio* — or the prebuilt VSIX from
**[the latest release](https://github.com/EstaxNet/Inferpal/releases/latest)**, by double-clicking
`Inferpal-vs2026-<version>.vsix`.

> [!IMPORTANT]
> Close Visual Studio before installing a VSIX. The merge that registers an extension rewrites the
> whole configuration at once and gives up if any part of it is in use, so installing with the IDE
> open can leave nothing registered — silently.

To build from source:

```powershell
# Debug (includes PDB in the VSIX, for Attach-to-Process debugging)
dotnet build Inferpal/Inferpal.csproj

# Release (optimized, no symbols, warnings-as-errors)
dotnet build Inferpal/Inferpal.csproj -c Release
```

Open the generated `.vsix` from `Inferpal\bin\Debug\net8.0-windows\` (or `Release\`) and
double-click it to install into Visual Studio.

> [!TIP]
> Contributors can build and deploy straight into the installed extension with
> `./deploy-dev.ps1` (silent first-install bootstrap, skip-if-fresh, hot apply without
> restarting VS) — see **[Development → Deploy (dev)](development.md#deploy-dev-and-status)**.

## 4. Open the tool window

In Visual Studio: **Tools → Inferpal**, or press **Alt+B** / **Alt+O** from
anywhere.

## 5. Configure and connect

1. Open **Inferpal Settings**.
2. Under **Connection**, pick the **provider** (Ollama / LM Studio / OpenAI-compatible), set
   the **Server URL** (Ollama default: `http://localhost:11434`) and an **API key** if the
   server needs one, then select a chat model.
3. Optionally set a separate **Code Actions model**, **FIM model**, and **embedding model**.
4. Enable **Semantic Indexing** to power `search_codebase`.
5. Click **Test** — it should report **Connected** and populate the model dropdown.
6. Switch to the **Inferpal** chat window, type a prompt, and press **Enter** (or click **↑**).

See **[Configuration](configuration.md)** for every available setting.

## VS Code

The VS Code extension shares the same engine and the same Inferpal configuration as the
Visual Studio extension — configure once, use in both editors. Since 1.2.0 it is at
**feature parity** with the Visual Studio front-end.

1. Install from the
   **[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=EstaxNet.inferpal-vscode)** —
   search *Inferpal* in the Extensions view, or run
   `code --install-extension EstaxNet.inferpal-vscode`. The backend (`Inferpal.Host`) is
   bundled and self-contained — no .NET installation required.

   To install by hand instead, take the VSIX for your platform from
   **[the latest release](https://github.com/EstaxNet/Inferpal/releases/latest)** —
   `inferpal-vscode-win32-x64-<version>.vsix` (Windows x64),
   `inferpal-vscode-linux-x64-<version>.vsix` (Linux x64) or
   `inferpal-vscode-darwin-arm64-<version>.vsix` (Apple Silicon) —
   `code --install-extension inferpal-vscode-<platform>-<version>.vsix`
   (or Extensions view → `…` → *Install from VSIX…*).
2. Open the **Inferpal** view in the Activity Bar (or press **Ctrl+Alt+I**) and start
   chatting: streaming replies with full markdown, the agentic loop with approvals and a
   live plan block, collapsible tool bubbles, typed `@`-mentions, slash-command
   autocomplete, inline FIM completions, unsaved-buffer awareness and live diagnostics from
   the Problems panel are all wired. Nearly all slash commands work headless through the
   host — see **[Slash Commands](slash-commands.md)** for the few still VS-only.
3. Configure through the **Inferpal Settings** panel (four tabs — Connection / Behavior /
   Context / Tools — backed by the configuration shared with Visual Studio). VS Code-side
   extension settings (`inferpal.*`) cover only `hostPath` (leave empty for the bundled
   host) and editor-local toggles such as `fim.enabled`.

To build it from source, see **[Development → VS Code extension](development.md#vs-code-extension)**.

## 6. Add project context (optional)

Create `.inferpal/context.md` at the root of your solution. Anything you write there —
conventions, architecture decisions, team rules — is injected into every system prompt.

- `/context` shows what is currently loaded.
- An edit applies from your next question: the file is read again before every one.

See **[Architecture → System prompt layering](architecture.md#system-prompt-layering)** for
the full injection order.

## Next steps

- **[Features](features.md)** — a tour of everything Inferpal can do.
- **[Slash Commands](slash-commands.md)** — the `/command` reference.
- **[Tools](tools.md)** — what the agent can do on its own.
