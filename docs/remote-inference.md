# Remote Inference Guide

Inferpal talks to its model server over plain HTTP, so the server does **not** have to be
the machine running Visual Studio. Pointing the extension at a remote box — a workstation
with a big GPU, a home server, another machine on your LAN — lets you keep Visual Studio
light while a beefier machine does the inference.

This works with **any** provider: **Ollama**, **LM Studio**, or any **OpenAI-compatible**
server. The only thing that changes per provider is *how you expose the server on the
network* and the *default port*. This guide covers both Ollama and LM Studio, points
Inferpal at the host, and explains the one setting you must configure by hand because the
backend can't report it: the **VRAM budget**.

| Provider | Default port | Inferpal Server URL | How to expose on the network |
|---|---|---|---|
| **Ollama** | `11434` | `http://<host>:11434` | `OLLAMA_HOST=0.0.0.0:11434` env var |
| **LM Studio** | `1234` | `http://<host>:1234` (a `/v1` suffix is also accepted) | **Serve on Local Network** toggle in the Developer/Server tab |
| **OpenAI-compatible** | varies | your `/v1` base URL | per-server (llama.cpp `--host 0.0.0.0`, vLLM, …) |

---

## 1. Expose the server on the remote host

### Option A — Ollama

By default Ollama only listens on `127.0.0.1:11434`, which is unreachable from other
machines. Bind it to all interfaces with the `OLLAMA_HOST` environment variable.

**Windows (remote host)**

```powershell
# Set it permanently for the current user, then restart Ollama
setx OLLAMA_HOST "0.0.0.0:11434"
# Quit Ollama from the tray icon and relaunch it, or:
# Stop-Process -Name ollama -Force ; ollama serve
```

**Linux / macOS (remote host)**

```bash
# One-off
OLLAMA_HOST=0.0.0.0:11434 ollama serve

# Persistent (systemd): add to the service unit
#   Environment="OLLAMA_HOST=0.0.0.0:11434"
# then: sudo systemctl daemon-reload && sudo systemctl restart ollama
```

### Option B — LM Studio

LM Studio ships a built-in server that speaks the OpenAI `/v1` wire (plus a native
`/api/v1/*` surface that Inferpal uses for model management and loaded-state). By default
it listens only on `localhost:1234`; you expose it to the LAN with a single toggle.

1. Open LM Studio and switch to the **Developer** tab (the server view).
2. **Load** the model(s) you want to serve (a tool-calling chat model, and optionally an
   embedding model for semantic search).
3. **Start** the server.
4. In the server settings, enable **Serve on Local Network** — this rebinds it from
   `localhost` to `0.0.0.0` so other machines can reach it. Note the **port** (default
   `1234`).

Prefer the CLI? `lms server start` boots the server; enable the *Serve on Local Network*
option in the app (or the app settings) so it binds to all interfaces.

> LM Studio has no `keep_alive` idle-unload, so a loaded model stays resident until you
> unload it or stop the server. That's usually what you want on a dedicated host.

### Open the firewall

Allow inbound TCP on the server's port — `11434` for Ollama, `1234` for LM Studio:

```powershell
# Windows (run as admin on the remote host) — adjust the port per provider
New-NetFirewallRule -DisplayName "Inferpal backend" -Direction Inbound -Protocol TCP -LocalPort 11434 -Action Allow
```

```bash
# Linux (ufw)
sudo ufw allow 11434/tcp   # Ollama
sudo ufw allow 1234/tcp    # LM Studio
```

### Verify from the client machine

From the machine running Visual Studio, confirm the host is reachable (replace the IP):

```powershell
# Ollama
curl http://192.168.1.2:11434/api/tags
# LM Studio (OpenAI-compatible)
curl http://192.168.1.2:1234/v1/models
```

A JSON list of models means you're good.

---

## 2. Point Inferpal at the remote host

1. Open **Inferpal Settings**.
2. Under **Connection**, set the **Provider** to match the remote server (**Ollama** or
   **LM Studio**) and set **Server URL** to the remote address:
   - Ollama → `http://192.168.1.2:11434`
   - LM Studio → `http://192.168.1.2:1234` (a trailing `/v1` is accepted and normalized)
3. Click **Test** — it should report **Connected** and the model dropdown should populate
   with the remote host's models.
4. Pick a chat model (and, optionally, a separate code-actions / FIM / embedding model).

Everything else — agent tools, inline completions, semantic indexing — works exactly as it
does locally; the requests just travel over the network.

> **Latency note.** Inline ghost-text completions are latency-sensitive. Over a fast LAN
> this is usually fine; over Wi-Fi or a VPN you may want to raise the completion preset to
> *Default* or *High Accuracy* (longer debounce) so fewer in-flight requests are cancelled.

---

## 3. Set the VRAM budget (required for remote hosts)

Neither backend reports the GPU's **total** VRAM over the network. Ollama exposes how much
VRAM each *loaded* model currently uses (`/api/ps`) but has no total-VRAM endpoint; LM
Studio reports loaded-state but not a per-model GB figure either. On a **local** host
Inferpal auto-detects the total via `nvidia-smi`; on a **remote** host that probe can't
run, so the total is unknown unless you tell it — regardless of provider.

The VRAM budget powers three things:

- the **fit-check** that warns on first run when the auto-picked chat + embedding models
  won't fit together,
- the **recommended max `num_ctx`** shown in `/hardware` (computed from the model's exact
  KV-cache cost), and
- the overflow warning when your configured `num_ctx` exceeds what the budget allows.

Set it either way:

- **Settings → Context → VRAM budget** — enter the remote GPU's total VRAM in GB (e.g. `24`).
  Leave it empty to keep auto-detection (local hosts only).
- **`/hardware <gb>`** in the chat — e.g. `/hardware 24`. Persists to config immediately.

Then run **`/hardware`** with no argument to see the full profile: budget, currently loaded
models, headroom, per-model VRAM estimates, and the context-window recommendation.

> The budget is a hint used for advice only. It never caps or overrides your settings —
> Inferpal will not stop you from running a large `num_ctx`, it just warns when the
> KV-cache is likely to spill into system RAM and slow generation.

---

## 4. GPU scheduling across a single backend

A remote host is typically a **single backend** (one Ollama or one LM Studio server) shared
by everything Inferpal does: chat/agent requests, background RAG indexing, `@Docs`
embedding, and inline completions. To stop background work from starving the interactive
model, Inferpal routes all of it through a central GPU scheduler:

- a chat/agent run takes a lease for its whole duration;
- RAG and `@Docs` embedding loops wait while a chat/agent run holds the lease and resume
  right after;
- inline completions (which run in the VS process) yield via an IPC busy-signal so they
  don't compete with an active chat.

The practical effect: the interactive model always loads first and you never wait 30
minutes for a prompt because indexing monopolised the remote GPU. This is provider-agnostic
— it applies whether the remote backend is Ollama or LM Studio.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| **Test** fails / Send button greys out | Host not reachable. For Ollama re-check `OLLAMA_HOST=0.0.0.0`; for LM Studio confirm **Serve on Local Network** is on. Re-check the firewall rule and the IP/port. Confirm with `curl http://<host>:11434/api/tags` (Ollama) or `curl http://<host>:1234/v1/models` (LM Studio) from the client. |
| Connects but no models listed | The server is running but no model is available. Ollama: `ollama pull <model>` on the host. LM Studio: **load** a model in the Developer tab. |
| `/hardware` shows "VRAM budget: not set" | Expected on a remote host with either backend — set it manually (section 3). Auto-detection only works on loopback. |
| First completion is slow, then fast | Cold model load on the remote host. With Ollama the model stays resident per its `keep_alive`; with LM Studio it stays resident until unloaded. Subsequent calls are fast. |
| LM Studio: model unloads itself unexpectedly | Check the app's auto-unload / JIT settings; LM Studio can evict idle models depending on configuration. |
| AMD GPU: driver crashes or corrupted output | Some ROCm builds are unstable on certain AMD cards. Check your backend's GPU / AMD documentation for supported cards, and keep both the backend and the GPU driver up to date. |

---

## Security note

Binding a backend to `0.0.0.0` exposes an **unauthenticated** inference API to everyone who
can reach that port — neither Ollama nor LM Studio has built-in auth on the local-network
server. Only do this on a trusted private network. To expose it beyond the LAN, put it
behind a reverse proxy that adds TLS and authentication (then use the OpenAI-compatible
provider with an **API key** if the proxy expects a bearer token), or tunnel it over a
VPN / SSH rather than opening the port to the internet.
