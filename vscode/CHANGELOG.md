# Changelog

All notable changes to the Inferpal VS Code extension. The extension and the Visual Studio
extension share one engine and one version number.

## 1.6.3

- Version alignment only, again — the two front-ends share one number. 1.6.2 shipped a fix for the
  Visual Studio package that did not work; 1.6.3 is the one that does. Nothing in either release
  touches the VS Code extension.

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
