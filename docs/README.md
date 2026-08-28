<p align="center">
  <img src="../Inferpal/assets/icon-256.png" alt="Inferpal" width="96" height="96">
</p>

# Inferpal Documentation

Inferpal turns a **local LLM** — served by [Ollama](https://ollama.com),
[LM Studio](https://lmstudio.ai), or any OpenAI-compatible server — into an **agentic
developer assistant** with tool calling, inline completions, semantic codebase search, and
zero required cloud dependency. It ships as a **Visual Studio 2026 extension** (the
primary target) and a **VS Code extension** (at feature parity since 1.2.0), both driven by
the same `Inferpal.Core` engine.

This folder is the full documentation set, split into **functional** guides (how to use it)
and **technical** references (how it works).

> [!TIP]
> New here? Start with **[Getting Started](getting-started.md)**, then skim the
> **[Features](features.md)** overview.

## Overview

```mermaid
flowchart LR
    user([You]) -->|prompt| chat[Inferpal chat]
    chat -->|tool calls| tools[28 built-in tools<br/>+ MCP + custom]
    chat <-->|HTTP| provider[Model server<br/>Ollama / LM Studio / OpenAI-compatible]
    tools --> ws[(Your workspace)]
    editor([VS editor]) -->|ghost text| chat
    chat -->|index| rag[(Semantic index)]
```

## Functional guides

| Guide | What it covers |
|---|---|
| [Getting Started](getting-started.md) | Install a backend, build & install the extension, first run |
| [Providers](providers.md) | Ollama / LM Studio / OpenAI-compatible — capabilities and setup |
| [Configuration](configuration.md) | Every setting and config key, with defaults |
| [Features](features.md) | Functional tour of everything Inferpal does |
| [Slash Commands](slash-commands.md) | The full `/command` reference |
| [Tools](tools.md) | The 28 built-in agent tools, custom shell tools, permission rules, and the approval model |
| [Mentions](mentions.md) | The `@` typed-context picker |
| [Search & Indexing](search-and-indexing.md) | Semantic codebase search (RAG) and `@Docs` external documentation |
| [MCP](mcp.md) | Connecting Model Context Protocol servers |
| [Rules & AI Checks](rules-and-checks.md) | Repo-versioned governance (`.inferpal/rules`, `.inferpal/checks`) |
| [Remote Inference](remote-inference.md) | Run the model server on another machine |

## Technical references

| Reference | What it covers |
|---|---|
| [Architecture](architecture.md) | Process model, IPC boundary, services, data flow, GPU scheduling |
| [Development](development.md) | Build, test, project layout, adding tools/languages, contributing |

## Quick facts

| | |
|---|---|
| Visual Studio | 2026 (18.x) — Community / Professional / Enterprise |
| VS Code | feature parity since 1.2.0 — win32-x64 VSIX with a bundled self-contained backend (`Inferpal.Host`) |
| Runtime | .NET 8 (`net8.0-windows`; core engine is plain `net8.0`) |
| Extension model | VS: `Microsoft.VisualStudio.Extensibility.Sdk` 17.14.x (in-process hosting since 2026-08-23) + in-process MEF for ghost text · VS Code: TypeScript + JSON-RPC host |
| Built-in tools | 26 (+ MCP servers + user shell tools) |
| Languages (UI) | 10 |
| Tests | 1188 xUnit tests |
