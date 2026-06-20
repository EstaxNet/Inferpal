# Remote Inference Guide

Inferpal talks to Ollama over plain HTTP, so the Ollama host does **not** have to be
the machine running Visual Studio. Pointing the extension at a remote box — a workstation
with a big GPU, a home server, another machine on your LAN — lets you keep Visual Studio
light while a beefier machine does the inference.

This guide covers how to expose a remote Ollama instance, point Inferpal at it, and the
one setting you must configure by hand because the Ollama API cannot report it: the **VRAM
budget**.

---

## 1. Expose Ollama on the remote host

By default Ollama only listens on `127.0.0.1:11434`, which is unreachable from other
machines. Bind it to all interfaces with the `OLLAMA_HOST` environment variable.

### Windows (remote host)

```powershell
# Set it permanently for the current user, then restart Ollama
setx OLLAMA_HOST "0.0.0.0:11434"
# Quit Ollama from the tray icon and relaunch it, or:
# Stop-Process -Name ollama -Force ; ollama serve
```

### Linux / macOS (remote host)

```bash
# One-off
OLLAMA_HOST=0.0.0.0:11434 ollama serve

# Persistent (systemd): add to the service unit
#   Environment="OLLAMA_HOST=0.0.0.0:11434"
# then: sudo systemctl daemon-reload && sudo systemctl restart ollama
```

### Open the firewall

Allow inbound TCP on the Ollama port (default `11434`) on the remote host:

```powershell
# Windows (run as admin on the remote host)
New-NetFirewallRule -DisplayName "Ollama" -Direction Inbound -Protocol TCP -LocalPort 11434 -Action Allow
```

```bash
# Linux (ufw)
sudo ufw allow 11434/tcp
```

### Verify from the client machine

From the machine running Visual Studio, confirm the host is reachable (replace the IP):

```powershell
curl http://192.168.1.2:11434/api/tags
```

A JSON list of installed models means you're good.

---

## 2. Point Inferpal at the remote host

1. Open **Inferpal Settings**.
2. Under **Connection**, leave the provider on **Ollama** and set **Server URL** to the
   remote address, e.g. `http://192.168.1.2:11434`.
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

The Ollama API exposes how much VRAM each **loaded** model currently uses (`/api/ps`), but
it has **no endpoint that reports the GPU's total VRAM**. On a local host Inferpal can
auto-detect it via `nvidia-smi`; on a remote host that probe can't run, so the total is
unknown unless you tell it.

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

A remote host is typically a **single Ollama backend** shared by everything Inferpal
does: chat/agent requests, background RAG indexing, `@Docs` embedding, and inline
completions. To stop background work from starving the interactive model, Inferpal
routes all of it through a central GPU scheduler:

- a chat/agent run takes a lease for its whole duration;
- RAG and `@Docs` embedding loops wait while a chat/agent run holds the lease and resume
  right after;
- inline completions (which run in the VS process) yield via an IPC busy-signal so they
  don't compete with an active chat.

The practical effect: the interactive model always loads first and you never wait 30
minutes for a prompt because indexing monopolised the remote GPU.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| **Test** fails / Send button greys out | Host not reachable. Re-check `OLLAMA_HOST=0.0.0.0`, the firewall rule, and the IP/port. Confirm with `curl http://<host>:11434/api/tags` from the client. |
| Connects but no models listed | Ollama is running but has no models pulled. On the remote host: `ollama pull <model>`. |
| `/hardware` shows "VRAM budget: not set" | Expected on a remote host — set it manually (section 3). Auto-detection only works on loopback. |
| First completion is slow, then fast | Cold model load on the remote host. The model stays resident per its `keep_alive`; subsequent calls are fast. |
| AMD GPU: driver crashes or corrupted output | Some ROCm builds are unstable on certain AMD cards. Check Ollama's own GPU / AMD documentation for supported cards and any experimental backends, and make sure both Ollama and the GPU driver are up to date. |

---

## Security note

Binding Ollama to `0.0.0.0` exposes an **unauthenticated** inference API to everyone who
can reach that port — Ollama has no built-in auth. Only do this on a trusted private
network. To expose it beyond the LAN, put it behind a reverse proxy that adds TLS and
authentication, or tunnel it over a VPN / SSH rather than opening the port to the internet.
