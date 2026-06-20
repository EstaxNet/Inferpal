# Providers

Inferpal talks to a model server over HTTP through a provider abstraction
(`IInferenceProvider`, resolved at startup by `InferenceProviderFactory`). You pick the
provider in **Settings → Connection**.

## Supported providers

| Provider | `provider` value | Endpoint style | Notes |
|---|---|---|---|
| **Ollama** | `ollama` | `POST /api/chat` (NDJSON) | Default. Full hardware-aware features. |
| **LM Studio** | `lmstudio` | OpenAI `/v1` | Local server; per-model resident VRAM is not reported, so the VRAM badge shows the model name without a GB figure. |
| **OpenAI-compatible** | `openai` | `POST /v1/chat/completions` (SSE) | Generic servers (llama.cpp, vLLM, …). Supports an optional **API key** (sent as `Authorization: Bearer …`). |

## Capability matrix

Each provider advertises a `ProviderCapabilities` record; features degrade safely when a
capability is missing.

| Capability | Ollama | LM Studio | OpenAI-compatible |
|---|:---:|:---:|:---:|
| Chat + tool calling | ✅ | ✅ | ✅ |
| Embeddings (semantic search) | ✅ | ✅ | ✅ |
| Inline completions (FIM) | ✅ | ✅ | ❌ |
| Model management (`/models` list/pull/delete) | ✅ | ✅ | ❌ |
| Live VRAM monitoring (`/api/ps`) | ✅ | ✅ | ❌ |
| `keep_alive` (auto-unload) | ✅ | ❌ | ❌ |

> [!NOTE]
> Streaming-pull progress in `/models` is an Ollama feature. Live per-model VRAM and the
> hardware-aware fit-checks rely on Ollama's `/api/ps` + `/api/show`; on other backends the
> manual **VRAM budget** still drives the `num_ctx` advice (see below).

## Configuring a provider

1. **Settings → Connection → Provider** — choose Ollama / LM Studio / OpenAI-compatible.
2. **Server URL** — e.g. `http://localhost:11434` (Ollama) or your `/v1` base URL.
3. **API key** — only needed for OpenAI-compatible endpoints that require auth.
4. **Test** — verifies connectivity and lists models.

## VRAM budget

The hardware-aware features (first-run fit-check, `/hardware`, recommended `num_ctx`) need
to know the GPU's **total** VRAM. On a local Ollama host this is auto-detected via
`nvidia-smi`; otherwise set it manually:

- **Settings → Context → VRAM budget** (in GB), or
- `/hardware <gb>` in the chat (e.g. `/hardware 24`).

Run `/hardware` with no argument for the full profile. When live monitoring isn't available
for the backend, Inferpal tells you and falls back to the manual budget for fit-checks.

## Remote hosts

Any provider URL can point at another machine. For a full walkthrough (exposing the host,
firewall, VRAM budget, GPU scheduling) see **[Remote Inference](remote-inference.md)**.
