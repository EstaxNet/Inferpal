# Inferpal for VS Code

AI developer assistant for **self-hosted LLMs** — Ollama, LM Studio, or any OpenAI-compatible server, on your machine or your own remote host. **100% local: no mandatory cloud, no telemetry, no API key required.**

## Features

- **Agentic chat** in the sidebar: the model reads and writes files, explores your codebase, runs commands and tests — with your approval on every destructive action (deny / allow once / always).
- **Inline completions** (ghost text) via Fill-in-the-Middle, cancellable mid-request.
- **Dirty-buffer awareness**: the agent sees your unsaved edits, not the stale on-disk file.
- **Live diagnostics**: the agent reads the Problems panel instantly instead of waiting for a build.
- **Backend-aware**: model picker, capabilities detection (model management, VRAM monitoring, FIM) per provider.

## Requirements

- A local LLM server: [Ollama](https://ollama.com) (default `localhost:11434`), [LM Studio](https://lmstudio.ai), or any OpenAI-compatible endpoint.
- The extension ships with its own self-contained backend (`Inferpal.Host`) — no .NET installation needed.

## Settings

| Setting | Description |
|---|---|
| `inferpal.hostPath` | Path to `Inferpal.Host` (leave empty to use the bundled one). |
| `inferpal.model` | Model to chat with (empty = host default). |
| `inferpal.agentMode` | Agentic loop with tools (on) or plain chat (off). |
| `inferpal.fim.enabled` | Inline ghost-text completions. |

Backend selection (Ollama / LM Studio / OpenAI-compatible URL) is configured in Inferpal's own config, shared with the Visual Studio extension.

## License

GPL-3.0 — source at [github.com/EstaxNet/Inferpal](https://github.com/EstaxNet/Inferpal).
