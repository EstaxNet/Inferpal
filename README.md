<p align="center">
  <img src="Inferpal/assets/icon-256.png" alt="Inferpal" width="120" height="120">
</p>

<h1 align="center">Inferpal</h1>

<p align="center">
  An agentic developer assistant for Visual Studio 2022/2026, powered entirely by
  <b>local LLMs</b> — Ollama, LM Studio, or any OpenAI-compatible server. Full tool
  calling, inline ghost-text completions, semantic codebase search, and zero required
  cloud dependency.
</p>

<p align="center">
  <a href="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml"><img src="https://github.com/EstaxNet/Inferpal/actions/workflows/ci.yml/badge.svg?branch=master" alt="CI"></a>
  <a href="https://github.com/EstaxNet/Inferpal/releases/latest"><img src="https://img.shields.io/github/v/release/EstaxNet/Inferpal" alt="Release"></a>
  <a href="https://www.gnu.org/licenses/gpl-3.0"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"></a>
  <img src="https://img.shields.io/badge/tests-969%20passing-brightgreen" alt="Tests">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/Visual%20Studio-2022%20%2F%202026-5C2D91" alt="Visual Studio 2022 / 2026">
</p>

---

## What is Inferpal?

Inferpal turns a local model into a fully agentic coding assistant living inside Visual
Studio. The model autonomously chains tool calls — reading and writing files, running
commands, building, testing, and searching your codebase — to complete real tasks, while
every write and every command stays behind an approval gate and a workspace sandbox. No API
key, no telemetry, no cloud required.

### Highlights

- **Agentic loop** — 26 built-in tools, plus user-defined shell tools and **MCP** servers; independent read-only tools run in parallel.
- **Local-first** — Ollama, LM Studio, or any OpenAI-compatible server (llama.cpp, vLLM); run the backend locally or on a [remote GPU host](docs/remote-inference.md).
- **Inline ghost-text completions** — Fill-in-the-Middle as you type (Tab / Esc), with Fast / Default / High-Accuracy presets.
- **Semantic codebase search** — background indexing with hybrid retrieval (cosine + BM25 fused with RRF) and per-turn auto-context.
- **Smart Fix Protocol** — after every edit, a polyglot build/typecheck (.NET / TypeScript / Rust / Go) feeds compile errors back so the agent fixes them in the same loop.
- **Code actions & Inline Edit** — Explain / Fix / Refactor / Add Tests / Add Docstring, plus **Ctrl+Shift+I** to rewrite a selection in place.
- **Safety by default** — approval-gated writes/commands, a non-bypassable catastrophic-command denylist, committable permission rules, and a hardened SSRF guard.
- **Governance & knowledge** — repo-versioned `.inferpal/rules` & AI checks, `@Docs` external-doc indexing, `@`-mentions, and 30+ slash commands.
- **Built for the IDE** — live debugger awareness, VRAM monitoring, VS theme adaptation, and 10 UI languages.

> See **[docs/features.md](docs/features.md)** for the full functional tour.

---

## Requirements

| Requirement | Details |
|---|---|
| Visual Studio | 2022 (17.9+) **or** 2026 (18.x) — Community / Professional / Enterprise |
| .NET SDK | .NET 8 |
| Model server | [Ollama](https://ollama.com) (default — full hardware-aware features), [LM Studio](https://lmstudio.ai), or any **OpenAI-compatible** server, local or [remote](docs/remote-inference.md) |

> Tool calling is required (e.g. `llama3.1`, `qwen2.5-coder`, `mistral-nemo`; `llama3` v1 does not).
> Inline completions need a FIM model; semantic search works best with a dedicated embedding model.

---

## Quick Start

```powershell
# 1. Start a model server (Ollama shown; LM Studio / OpenAI-compatible also work)
ollama serve
ollama pull llama3.1

# 2. Build the extension
dotnet build Inferpal/Inferpal.csproj
```

3. Double-click the generated `.vsix` in `Inferpal\bin\Debug\net8.0-windows\` to install.
4. In Visual Studio open **Tools → Inferpal** (or **Alt+B** / **Alt+O**).
5. Open **Inferpal Settings**, pick the provider, set the server URL, select a model, click **Test**, then start chatting.

Full walkthrough: **[Getting Started](docs/getting-started.md)**.

---

## Documentation

Complete functional and technical documentation lives in **[`docs/`](docs/README.md)**.

| Functional | Technical |
|---|---|
| [Getting Started](docs/getting-started.md) · [Providers](docs/providers.md) · [Configuration](docs/configuration.md) | [Architecture](docs/architecture.md) |
| [Features](docs/features.md) · [Slash Commands](docs/slash-commands.md) · [Tools](docs/tools.md) · [Mentions](docs/mentions.md) | [Development](docs/development.md) |
| [Search & Indexing](docs/search-and-indexing.md) · [MCP](docs/mcp.md) · [Rules & Checks](docs/rules-and-checks.md) · [Remote Inference](docs/remote-inference.md) | |

---

## Contributing

Contributions are welcome — see **[Development](docs/development.md)** for the build, the
project layout, and how to add a tool or a language. Quick version: implement `ITool`,
register it in `ToolRegistry.cs`, and add any new strings to all 10 `.resx` files **and** to
`Strings.cs`.

---

## License

Licensed under the [GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0).

## Acknowledgments

Developed with the assistance of **Claude Opus 4.8** (Anthropic).
