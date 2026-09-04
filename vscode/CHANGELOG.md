# Changelog

All notable changes to the Inferpal VS Code extension. The extension and the Visual Studio
extension share one engine and one version number.

## 1.6.8

- **A numeric setting that could not be read is now named, instead of being dropped in silence.**
  Saving still goes through — refusing the whole form would throw away the other, valid edits you
  just made — and the status line says which boxes were skipped: *"Settings saved. 2 field(s)
  ignored (unreadable value): Max iterations, Results per query"*. The reading itself was wrong
  too: `parseInt('12abc')` is **12**, so a mistyped box did not get ignored — it silently stored a
  truncation you never typed. Numbers are read strictly now. An empty box is never reported:
  clearing one is a deliberate gesture, not a lost value.
- **Four gestures did nothing at all when the host was not running**: "save / load / delete
  session" from the command palette, the ↻ button in the settings panel, `/branch <name>`, and —
  worst — **Save** in the settings panel, which left the panel showing its previous status, so
  "Settings saved." if you had saved once before. Nothing was written and nothing said so. All
  four now name the real cause: host stopped, or no folder open.
- **A document that grew past the mirroring ceiling stayed stale in the model's view.** A file
  opened under 1 MB and then grown past it (a paste, an appended log) left the assistant reading
  its last version under the ceiling for as long as the file stayed open — and editing on top of
  that — with nothing saying so.
- **Typing is cheaper.** The size guard built the whole document on every keystroke to decide
  whether the document was too big to build, ahead of the debounce whose job is to keep typing
  cheap; the debounced callback then built it a second time. Same shape in the inline-completion
  provider.
- **Under Linux, `a.cs` and `A.cs` are no longer the same breakpoint.** Case folding is a property
  of the file system, not of the process, and two path comparators folded it unconditionally — so
  removing one breakpoint removed the other, and the debugger tools reported the wrong file to the
  model.
- **"The model returned no response" no longer blames the model.** It said the configured model
  might not support text generation and advised switching models; measured against the server it
  accused, that server was serving that very model perfectly. The message now states what was
  observed — which model, which server, request accepted and stream closed without a single token,
  so not a connection problem — and what the stream contained is recorded for `/diagnostics`.
- **`/undo-run` reverts only the run you watched.** A tracking run was opened but never closed, so
  anything written afterwards — a `/restore`, a tool launched from a slash command — still
  attached to it.
- Step mode, the agent-pause message and `/resume` are **translated** in all ten languages; they
  were English literals, three lines below their already-translated plan-mode twins.

## 1.6.7

- **LM Studio behind a reverse proxy listed no model at all, while the badge said connected.**
  The connection badge probes `{base}/v1/models` — the surface the chat actually talks — but the
  model list came only from LM Studio's native API `{base}/api/v1|v0/models`. A server that
  serves only the OpenAI-compatible surface therefore answered *Connected* with zero models, and
  an empty list is invisible: the picker puts the configured model back, which looks exactly like
  a backend serving one model. The list now falls back to the OpenAI-compatible surface when the
  native one answers nothing, and the chat picker says so when nothing was listed at all.
- **The model fields in the settings panel list every model again, not just the one already in
  the box.** They were `<input list="models">`, and the browser filters a `<datalist>` against
  what the field already contains: a field holding a model id offered exactly that id, and no
  gesture showed the others — while the Visual Studio window, a combo box, always lists them all.
  The host was never at fault: driving it over JSON-RPC, `models/list` returns the backend's full
  list, and the extension's output log carries no failure. Each model field now has a caret that
  opens the complete list; typing still narrows it, and a model the backend does not list can
  still be typed by hand.

## 1.6.6

- **A first install with no folder open is no longer told to restart the host.** Without a
  workspace folder the extension deliberately never starts one — the workspace root is a required
  handshake parameter — but the chat and the settings panel still advised *« Inferpal: Restart
  Host »*, which cannot help in that state. Both now say what is actually missing and offer
  *Open Folder*, in all ten languages.
- The nine localization bundles are now guarded by a test: a key added to eight of them used to
  ship as a half-translated UI, silently.
- The rest of this release is in the Visual Studio package: it had stopped carrying Roslyn since
  1.6.0, which left its semantic index holding no C# file at all. The VS Code extension was never
  affected there — its host is published self-contained, so `Microsoft.CodeAnalysis` ships with it
  and always has.

## 1.6.5

- **`MessagePack` is pinned to the patched 2.5.301** wherever it was still resolving to a
  vulnerable 2.5.192. The rest of this release is in the Visual Studio package: its dependency set
  is back to what it was in 1.6.1 plus the SQLite engine, and its listing description now fits the
  200 characters the Marketplace keeps.

## 1.6.4

- Version alignment only — the two front-ends share one number. The change in 1.6.4 is in the
  Visual Studio package manifest, which the Marketplace refused to accept.

## 1.6.3

- **A finished background task could be listed twice.** `/task` moved a task out of "running" and
  into "finished" under two different locks; in between it was in both, so `/task list` returned it
  twice and the queue counted it twice. One transition, one lock. This one is in the shared engine,
  so it affects VS Code as well.
- The other fix in 1.6.3 is in the Visual Studio package only (1.6.2 shipped a repair that did not
  work). Nothing there touches this extension.

## 1.6.2

- Version alignment only — the two front-ends share one number. The fix in this release is in the
  Visual Studio package, which shipped `Microsoft.Data.Sqlite` without the SQLitePCLRaw provider or
  the native `e_sqlite3`. The VS Code build carries its own self-contained host and has always
  shipped both, so nothing here changes for you.

## 1.6.1


- **The listing you are reading.** The Marketplace page is the README bundled in the VSIX, and it
  had never been written for VS Code — it described the July build, with no icon and with features
  that only exist in the Visual Studio front-end. Rewritten against the code, and the extension now
  ships an icon. The one-line description under the title was rewritten in the same pass,
  in the ten languages: it now names what sets the agent apart instead of only what it is.
- **The extension is now titled *Inferpal for VS Code*, and its identifier is
  `EstaxNet.inferpal-vscode`.** The Visual Studio build ships as *Inferpal for Visual Studio*
  under `EstaxNet.inferpal-vs`: the two editors share one Marketplace namespace, which is
  case-insensitive, so a single short name could not serve both. ⚠ If you installed the earlier
  `EstaxNet.inferpal`, that listing no longer exists — install this one and remove the old
  entry; settings and saved conversations are untouched, they live outside the extension.
- **Approval covers every path of a multi-file tool.** A `deny` rule was matched against all paths
  joined together, so a protected file was only caught when it happened to be last. Each path is
  now evaluated on its own: denied if any is denied, auto-approved only if all are.
- **Three tools that write files now ask.** `insert_at_cursor`, `replace_selection` and
  `update_memory` mutated without an approval prompt — and `update_memory` writes the file that is
  injected into the system prompt of every later session.
- **A shell subject can no longer stall the approval path.** The built-in command patterns had no
  match budget: a 64 KB command took 49 s to classify, with no prompt and no error. Every pattern
  now carries a timeout, and a pattern that cannot finish counts as *opaque* — a human prompt,
  never a silent pass. `rm -fr /`, which the old pattern missed entirely, is covered.
- Loading a saved session during a run no longer swaps the conversation under the running loop.

- **`run_tests` no longer reports "passed" from an exit code alone.** Four parsers (dotnet, pytest,
  cargo, go) fell back to "exit 0 means green" when they could not read a summary — so a run where
  nothing executed (no test project, a runsettings that skips everything, a moved summary format)
  was announced as a green suite, and `/tdd` stopped on it. A run without a readable summary is now
  reported as such, without the ✓ that `/tdd` reads as success.
- Three tests were Windows-only, and one of them was hiding a real POSIX defect in the `/tdd`
  guard that stops the agent from rewriting a test to make it pass.

## 1.6.0

- **`/tdd` gained a debugger.** When a test fails, the loop no longer reads the runner's text and
  guesses: on the first red round the failing test is re-run under the debugger — through the Debug
  Adapter Protocol, with a `coreclr` configuration and no `launch.json` needed — and the real
  exception, call stack and **expanded local values** go into the fix prompt. On a fixed 12-case
  bench with a local 27B model: **12/12 fixed against 10/12** without the capture, and on the two
  cases where the text-only loop burns all five rounds, the fix lands on round one.
  Running a test under a debugger is execution, so it asks **once per run**, not once per round, and
  degrades cleanly — a degraded round says so instead of passing itself off as an ordinary one.
- **Writing a test file during a `/tdd` run always asks**, bypassing every auto-approval path
  (allow rules, session grants, MCP tools) for the duration of the run.
- **A project's `deny` beats the machine's `allow`.** The committable `.inferpal/permissions.json`
  overlay is deny-only by design, but machine rules were evaluated first and won — a project could
  not tighten its own restrictions, which is the one thing the overlay exists for.
- **File encoding and BOM survive an edit.** `write_file`, `apply_diff`, `apply_edits` and
  `rename_symbol` rewrote files as UTF-8 without BOM: a one-line diff stripped the BOM from a whole
  file, and UTF-16/ANSI files were converted outright.
- **`restore_file` no longer restores the wrong file.** Snapshots were keyed by file name alone, so
  two `Config.cs` in different folders overwrote each other — with a plausible-looking diff at the
  approval prompt.
- **A long agent turn no longer silently loses its head.** Token estimation ignored tool-call
  arguments, so a run writing large files never triggered compaction and the backend truncated the
  conversation from the top instead.
- **A cancelled turn no longer leaves a live approval card behind** — it stayed clickable after the
  turn was gone, and approving it acted on a request nobody was waiting for.
- **Network errors tell the truth**: they named Ollama on every backend, announced a 30-minute
  timeout when the real deadline is 120–600 s, and reported a server that answered with an HTTP
  error as unreachable. Fixed in all 10 languages, and a 4xx/5xx body is surfaced instead of dropped.
- `get_git_status` is confined to the workspace like every other tool; MCP tools honour the current
  run's approval decorator; `/task stop` during the GPU wait actually stops the task; the semantic
  index no longer misses files changed during the initial pass; `analyze_impact` precision on
  TypeScript/JavaScript goes from 0.56 to 1.00 on the reference bench.

## 1.5.0

- The extension ships for **Windows x64, Linux x64 and Apple Silicon**, each build bundling its own
  self-contained backend — no .NET installation required. `run_command` speaks PowerShell on
  Windows and bash on POSIX, with the same persisted working directory and environment.

## 1.2.0

- **Feature parity with the Visual Studio front-end**: full markdown chat, agentic loop with
  approvals and a live plan block, collapsible tool bubbles, typed `@`-mentions, slash-command
  autocomplete, inline FIM completions, prompt history, token counter and context gauge,
  conversation search, `.md`/`.txt` export, a four-tab settings panel, and a webview localized in
  10 languages.
