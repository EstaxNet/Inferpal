# Getting Started

This guide takes you from nothing to a working Inferpal chat in Visual Studio.

## 1. Requirements

| Requirement | Details |
|---|---|
| Visual Studio | 2022 (17.9+) **or** 2026 (18.x) — Community, Professional or Enterprise |
| .NET SDK | .NET 8 |
| Model server | [Ollama](https://ollama.com) (default — full hardware-aware features), [LM Studio](https://lmstudio.ai), or any **OpenAI-compatible** server (llama.cpp, vLLM, …) |

> [!IMPORTANT]
> Tool calling is required for the agent. Any model that supports it works
> (e.g. `llama3.1`, `qwen2.5-coder`, `mistral-nemo`). `llama3` v1 does **not**.
>
> - **Inline completions** need a model that supports FIM (e.g. `qwen2.5-coder`, `deepseek-coder`).
> - **Semantic search** works best with a dedicated embedding model (e.g. `nomic-embed-text`, `mxbai-embed-large`).

## 2. Start a model server

Pick whichever backend you already use — see **[Providers](providers.md)** for the
differences.

- **Ollama** (default `:11434`)
  ```powershell
  ollama serve
  ollama pull llama3.1
  # Optional, dedicated models:
  ollama pull qwen2.5-coder:7b   # inline completions
  ollama pull nomic-embed-text   # semantic search
  ```
- **LM Studio** (default `:1234`)
  1. Open the **Developer** tab (the server view) and **Start** the server.
  2. **Load** a tool-calling chat model — and, optionally, a FIM model (`qwen2.5-coder`) and
     an embedding model for semantic search.
  3. Inferpal uses LM Studio's native `/api/v1/*` API for the model list and load/unload, so
     `/models` works here too. Point the **Server URL** at `http://localhost:1234`.
- **OpenAI-compatible** — expose the server's `/v1` endpoint (and an API key if it needs one).

The backend does not have to run on the machine hosting Visual Studio — see
**[Remote Inference](remote-inference.md)**.

## 3. Build and install the extension

```powershell
# Debug (includes PDB in the VSIX, for Attach-to-Process debugging)
dotnet build Inferpal/Inferpal.csproj

# Release (optimized, no symbols, warnings-as-errors)
dotnet build Inferpal/Inferpal.csproj -c Release
```

Open the generated `.vsix` from `Inferpal\bin\Debug\net8.0-windows\` (or `Release\`) and
double-click it to install into Visual Studio.

> [!TIP]
> During development you can deploy straight to the VS experimental hive with
> `./deploy-debug.ps1` (it auto-detects the hive).

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

## 6. Add project context (optional)

Create `.inferpal/context.md` at the root of your solution. Anything you write there —
conventions, architecture decisions, team rules — is injected into every system prompt.

- `/context` shows what is currently loaded.
- `/clear` reloads the prompt after you edit the file.

See **[Architecture → System prompt layering](architecture.md#system-prompt-layering)** for
the full injection order.

## Next steps

- **[Features](features.md)** — a tour of everything Inferpal can do.
- **[Slash Commands](slash-commands.md)** — the `/command` reference.
- **[Tools](tools.md)** — what the agent can do on its own.
