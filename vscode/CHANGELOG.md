# Changelog

All notable changes to the Inferpal VS Code extension. The extension and the Visual Studio
extension share one engine and one version number.

## 1.6.11

The first release driven by reports from people other than the maintainer. Most of what follows is
the same theme as 1.6.10: cases where Inferpal answered, and the answer was not true.

- **A new `/permissions` command shows the rules actually in force.** A project can ship a
  `.inferpal/permissions.json` to restrict what the assistant may do in it — and that file could
  stop applying **entirely** without anything visible saying so: invalid JSON, a missing `rules`
  array, one mistyped line. It was also the only one of the four project files with no way to list
  it, while being the one that *restricts* rather than advises. `/permissions` now shows the
  workspace overlay and your per-machine rules in the order they are evaluated, names an overlay it
  could not read, and separately reports `allow` rules — which a project overlay is not allowed to
  grant, by design rather than by mistake.
- **An MCP server that failed to start said nothing at all.** You add a server, its tools never
  appear, and there was no message, no diagnostic entry, nothing — just missing tools. In VS Code
  there was no place at all that showed the reason. Every failure is now recorded in
  `/diagnostics`, and *needs authorization* is kept apart from *did not start*. The
  `/diagnostics export` bundle lists each server with its tool count or its error.
- **The approval prompt showed nothing at all on files longer than 300 lines.** Before the assistant
  writes to a file, Inferpal asks you first and shows the change — that prompt is the only moment
  you see what you are agreeing to. It stopped showing anything past 300 lines: a single changed
  line in a 400-line source got you one sentence where the change should have been. The limit was
  measuring the size of the *file* rather than the size of the *change*.
- **"Build failed — 20 errors" when there were eighty.** After a file write Inferpal can run a quick
  build; both the assistant's copy and the banner in the chat counted the error list *after*
  trimming it for display, so any build with more errors than the display limit reported the limit
  as the total.
- **Analysis tools cut their answer short without saying so.** The call-graph tool built its index
  of definitions from a capped scan and then labelled everything it had not indexed as *external to
  your codebase* — a method living in your own repository reported as belonging to some library.
  Cross-language reports listed at most ten unmatched calls out of however many there were.
- **The assistant was told things about your machine that were not true**, and a connection failure
  now names the backend it was configured for instead of only blaming the server.
- **Three messages in the diff view were never translated** — one of them was hard-coded in French
  for every language.

## 1.6.10

The largest release of the 1.6 line, and none of it came from a bug report. Nothing crashed: a tool
that did a different thing and called it done, a setting that named the wrong editor, a list that had
quietly stopped matching the product — each looked like ordinary behaviour until it was measured.

- **The support bundle could publish a token you never meant to share.** `/diagnostics export`
  builds the file you paste into a bug report, and it was careful with the place a secret is
  *expected* — your API key printed as *set (redacted)*. But it also included the recent diagnostics
  verbatim, and that is where one actually turns up: a blocked or confirmed command is recorded with
  its full text, so a `curl` carrying an `Authorization` header or a URL with a password went in
  exactly as written. Credential-shaped text is masked on the way out now. `/diagnostics` on screen
  is unchanged — you are debugging your own machine there.
- **Approving a file write once no longer lets the assistant rewrite its own instructions.** Four
  files are fed back into the system prompt on every later session: your project context, its
  memory, your notes, your rules. Once you had answered *Always* to a write, later writes to those
  four went through without asking. They always ask now, whatever you allowed earlier — and they
  only **ask**: writing memory is a normal thing to do, it just has to be visible.
- **A multi-file edit could apply some of its edits and report success.** The tool promises that if
  any edit cannot be applied, no file is changed. That held for an edit that did not match your
  file, but an edit the assistant wrote incorrectly was dropped in silence and the others applied —
  *"Applied 2 edits"* on a batch of three. A single-file edit missing its replacement text deleted
  the matched block instead of refusing.
- **Three tools ignored an option unless it was spelled in lower case with no stray spaces.** You
  saw work that looked finished and was not: a call-graph report with no sections at all, a scan of
  the whole workspace concluding there are no links between your C# and your TypeScript, and
  *replace the memory* quietly appending instead.
- **The assistant was told it was running in Visual Studio.** It answered with Solution Explorer and
  the Build menu, and on Linux and macOS it was told to write PowerShell — so it opened with
  `Get-ChildItem` and spent turns rediscovering bash. The facts about your editor, your OS and your
  shell are built at runtime now. Asked about your debugger it could answer *"no paused session"*
  while you were stopped at a breakpoint; asked whether you had a file open it could say no while
  you were looking at one.
- **The settings panel said things that do not apply here.** The language setting claimed to
  override *Visual Studio*, the backend setting said changes take effect after *reloading Visual
  Studio* — a step you cannot perform — and the custom-tools field asked for PowerShell on machines
  where the shell is bash.
- **Ghost text had been dead on every VSIX install since 1.6.6.** The completion sidecar is a
  separate process and the package was missing one assembly it needed to start.
- **Mistyping a command answered with a list written out by hand.** It named 26 of the 57 commands
  that ship and offered one that no longer exists. It is generated now, from the same table `/help`
  uses.
- **Losing the backend mid-session was never announced** — a dot changed colour, and you learned
  about the outage from your next message failing. A reasoning model's thinking phase showed a
  frozen indicator, and exporting a conversation dropped everything but the messages, with the
  *Text* format writing Markdown into a `.txt` file.
- **Your own turns were labelled in French in exported conversations**, whatever your language, and
  two messages plus the context-window gauge stayed in English in all ten.

## 1.6.9

A release given to **things that were quietly not happening**: settings with no effect, capabilities
that fell back without a word, and answers that read as finished when they were not.

- **Long conversations were never bounded, and three settings that promised to bound them did
  nothing.** The history simply grew until it went past the model's context window, at which point
  the backend dropped the head of the conversation — system prompt included — without a word: an
  assistant that quietly forgets. Meanwhile the settings panel offered *Compact the conversation*,
  *Turns to keep* and the compaction timeout, none of which had any effect here. The check now runs
  in this editor too, from the same implementation as the Visual Studio window, and says what it
  did: turns dropped, turns replaced by a summary, or a summary that did not arrive in time and
  turns dropped instead.
- **A turn that produced no text ended in silence.** No answer, no message, nothing to act on. You
  now get the answer, or a summary of the tools that ran, or a message naming the model and the
  server that returned nothing.
- **An agent run that was cut short read exactly like one that finished.** When the loop hits its
  iteration limit, or is stopped because the model kept repeating the same tool calls, what you get
  is a summary of what it had gathered — and it arrived with nothing to say so. A short line now
  follows the answer. The answer itself is untouched.
- **Clearing a numeric setting did nothing.** Emptying a numeric box is how you restore its factory
  value; the panel did not know those values, so the gesture silently changed nothing.
- **"No relevant code found" was also what you got when the search never ran properly.** When the
  query cannot be embedded — the embedding model was never pulled, the backend is down — only the
  keyword half runs. An empty result now says so, and keeps *semantic search is off* apart from
  *the embedding model did not answer*.
- **The connection badge went green on any 2xx**, including from a server that answers "unknown
  endpoint" — so a client pointed at the wrong kind of backend looked connected while no turn could
  complete.
- **A `config.json` or a `snippets.json` that could not be read was not just ignored — the next save
  destroyed it.** The fallback stays, but the file is now copied aside first, and `/diagnostics`
  says what happened.
- **A rule you wrote could stop constraining the model, and nothing said so.** Unreadable files in
  `.inferpal/rules`, `.inferpal/checks` and `.inferpal/prompts` are now named in their own listing.
- **Permission rules, custom tools, command templates and MCP entries that could not be read** were
  dropped without a word. They are now reported — permission rules in the save status, rejected MCP
  entries in the server list, all of them in `/diagnostics`.
- **Semantic indexing could silently fall back to the heuristic chunker for a whole session** when
  no language server was on `PATH` or one died. It now says which of those happened, once, and
  names the executables it looked for.

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
