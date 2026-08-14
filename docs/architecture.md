# Architecture

This page describes how Inferpal is put together. For build/test/contribution mechanics see
**[Development](development.md)**.

## Two front-ends, one engine

All the logic — providers, tools, agent orchestrator, RAG, MCP, localization — lives in
**`Inferpal.Core`**, a plain `net8.0` class library with **zero editor-SDK or WPF
dependency** (enforced by `CoreIsolationTests`). Editor access goes through ports:
`IEditorSurface` for the editor surface and `ApprovalServiceBase` for the approval
pipeline.

Two adapters consume it:

- **Visual Studio** (`Inferpal/`) — the primary target, described in the rest of this page
  (`VsEditorSurface`, `VsApprovalService`, Remote UI tool window, in-process MEF ghost text).
- **VS Code** (`vscode/` + `Inferpal.Host/`, at feature parity since 1.2.0) — `Inferpal.Host` is a console
  process hosting the Core behind **header-framed JSON-RPC on stdio** (`initialize`,
  `chat/send` with streamed notifications, `models/list`, `fim/complete`, `textDocument/did*`
  sync…), with reverse ports `RpcEditorSurface` and `RpcApprovalService` (fail-closed). The
  TypeScript extension spawns and supervises it, renders the sidebar webview chat, and feeds
  dirty buffers through `OpenDocumentOverlay` so `read_file` sees unsaved edits.

## Process model

Inferpal uses the **out-of-process** Visual Studio Extensibility model
(`Microsoft.VisualStudio.Extensibility.Sdk` 17.14.x). A hard constraint of VS Remote UI:
only types loaded in `devenv.exe` can be referenced in XAML. The out-of-process parts run in
a `ServiceHub.Host` process, so all data crossing the boundary must be `[DataContract]`
objects containing **only primitives** (and collections of such).

```mermaid
flowchart LR
    subgraph host["Extension process — ServiceHub.Host"]
        prov[IInferenceProvider clients<br/>Ollama / LM Studio / OpenAI]
        reg[ToolRegistry — 26 tools + MCP]
        md[MarkdownParser → MarkdownBlock + InlineRun]
        rag[ProjectIndexService — hybrid RAG: cosine + BM25/RRF]
        vm[InferpalToolWindowData — ViewModel]
    end
    subgraph dev["VS host process — devenv.exe"]
        wpf[WPF DataTemplate rendering<br/>DataTrigger on MarkdownBlock.Type]
        ghost[GhostText MEF<br/>adornments → IWpfTextView]
    end
    host -- "IPC: [DataMember] primitives only" --> dev
```

### In-process ghost text

Inline completions need `IWpfTextView`, which is not available to out-of-process extensions.
The `GhostText` components are therefore **in-process**: MEF parts
(`IWpfTextViewCreationListener`, `AdornmentLayerDefinition`) plus a minimal `AsyncPackage`
that forces Visual Studio to load `Inferpal.dll` inside `devenv.exe`. They ship in the **same
VSIX and assembly** as the out-of-process extension but run in `devenv.exe`.

## Agentic loop

```mermaid
flowchart TD
    p[User prompt] --> req[POST chat endpoint<br/>model, messages, tools, stream]
    req --> stream[Stream tokens in real time]
    stream --> done{done?}
    done -- "tool_calls" --> exec[Execute tool → append result]
    exec --> smart{write_file / apply_diff / apply_edits?}
    smart -- yes --> fix[Smart Fix: build/typecheck → errors inline]
    fix --> req
    smart -- no --> req
    done -- "no tool calls (max 20 turns)" --> final[Final answer]
    final --> render[Parse Markdown → structured blocks]
```

The chat endpoint is provider-specific (`/api/chat` for Ollama, `/v1/chat/completions` for
OpenAI-compatible); the loop logic is identical. `RunAgentAsync` never throws on network
errors — it converts them into a result; only cancellation propagates.

## System prompt layering

`BuildSystemPrompt()` assembles the prompt in this order at the start of each conversation:

```
[Base prompt]          ← hardcoded in the extension
[Custom system prompt] ← Settings (optional)
[Pinned files]         ← up to 3 pinned context files (optional)
[## Project context]   ← .inferpal/context.md (optional)
[## Agent memory]      ← .inferpal/memory.md (optional)
[## Project notes]     ← .inferpal/notes.md (optional)
[## Rules]             ← .inferpal/rules/*.md matching the active file (optional)
```

It is rebuilt on `/clear`, on session load, and when the active editor file changes (so
glob-scoped rules and the persona re-scope automatically).

## GPU scheduling

Chat, ghost-text FIM, and RAG/@Docs embeddings can all target one backend on one GPU.
Without coordination a steady stream of background embeddings can starve the chat model and
the request times out. A central scheduler enforces priority **chat > FIM > embedding**:

- A chat/agent run acquires a **lease** (`GpuScheduler`) for its whole duration — this covers
  chat, commit/title generation, synthesis, plan, and code actions.
- Background embedding loops (`ProjectIndexService`, `DocsIndexService`) `await
  WaitForChatIdleAsync` — they pause without losing progress and resume immediately.
- Inline completions run in `devenv.exe`, so they coordinate cross-process via `ChatBusySignal`
  markers (`%TEMP%/Inferpal/chat_busy.<pid>.json`, **one file per writer** — pid + timestamp,
  anti-stale, periodically re-stamped during long runs): FIM is skipped while *any* live marker
  is fresh. Each writer only ever deletes its own marker, so the first chat to finish cannot
  silence another's. A legacy unscoped `chat_busy.json` from an older version is still honoured
  read-only, never written or deleted.

> [!WARNING]
> **Query** embeddings (`search_codebase`, `search_docs`) are never gated — the agent holding
> the lease would otherwise deadlock on its own embedding. Only background **indexing** loops
> wait.

## Rendering & theming

- **Markdown**: `MarkdownParser` (Markdig 1.2.0) parses assistant text into a
  `List<MarkdownBlock>` (stripping `<think>…</think>`), propagated as `[DataMember]` to
  `devenv.exe` and rendered by `DataTrigger` on `MarkdownBlock.Type` (paragraph/lists →
  `WrapPanel`, headings → `TextBlock`, `code_block` → read-only `TextBox`, separator →
  `Border`).
- **Theme**: detected from VS settings and propagated VM → `ChatMessageItem` → `MarkdownBlock`
  → `InlineRun` as plain `[DataMember]` color strings (Remote UI does not propagate via
  `ElementName` across nested `DataTemplate`s). Colors are centralized in `ThemePalette`.

## Cross-process signals

The in-process package publishes state the out-of-process agent reads via small file-based
IPC channels:

| Signal | Direction | Scope | Purpose |
|---|---|---|---|
| `ChatBusySignal` | host → devenv | machine-wide, one marker file per writer | FIM yields to an active chat anywhere on the box (one GPU) |
| `DebuggerStateSignal` | devenv → host | per VS instance (`<name>.<devenv pid>.json`) | `VsDebuggerTracker` publishes the break state for `get_debugger_state` / `@debugger` |
| `DebugCommandSignal` | host → devenv | per VS instance (`<name>.<devenv pid>.json`) | carries `/debug` operations (breakpoints, step, continue…) to the in-process EnvDTE driver |
| `BuildSignalFile` | devenv → host | per VS instance (`<name>.<devenv pid>.json`) | `VsBuildMonitor` surfaces VS build failures as the "Build Failed" banner |
| `ActiveSolutionSignal` | devenv → host | per VS instance (`<name>.<devenv pid>.json`) | authoritative open-solution root for `/solution`, `/map`, RAG |
| `InlineDiffPreviewSignal` | host → devenv | per VS instance (`<name>.<devenv pid>.json`) | carries inline-diff preview requests to the in-editor renderer |

Per-instance channels key their file names on the devenv PID (the in-process package declares
its own PID; the extensibility host declares its parent PID after checking the parent is
`devenv`), so two VS instances never read each other's state. A process that declared no key
keeps the legacy unscoped names; a host with no in-process peer (VS Code) does not read the
per-instance channels at all.

## Tech stack

| Component | Technology |
|---|---|
| Language / runtime | C# .NET 8 (`net8.0-windows`) |
| Extension SDK | `Microsoft.VisualStudio.Extensibility.Sdk` 17.14.40608 (out-of-process) |
| In-process MEF | `Microsoft.VisualStudio.Shell.15.0`, `Microsoft.VisualStudio.Text.UI.Wpf` |
| Markdown | Markdig 1.2.0 |
| Vector store | SQLite WAL (`Microsoft.Data.Sqlite` 8.0.16) |
| C# analysis | `Microsoft.CodeAnalysis.CSharp` 4.14.0 (Roslyn) |
| MCP client | Home-grown JSON-RPC 2.0 — stdio + Streamable HTTP (with OAuth 2.1), zero extra dependencies |
