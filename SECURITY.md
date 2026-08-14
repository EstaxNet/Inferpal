# Security Policy

## Supported versions

Inferpal is distributed from the `master` branch and the
[latest release](https://github.com/EstaxNet/Inferpal/releases/latest). Security fixes land on
`master`; please reproduce on the latest build before reporting.

## Reporting a vulnerability

Please report security issues **privately** — do not open a public issue.

- Preferred: **[Report a vulnerability](https://github.com/EstaxNet/Inferpal/security/advisories/new)**
  (GitHub private vulnerability reporting).
- Or email **estaxnet@gmail.com** with steps to reproduce and the affected version.

You'll get an acknowledgement within a few days. Once a fix is available, the advisory is
published with credit to the reporter unless you prefer to stay anonymous.

## Security model — what to know

Inferpal is local-first and runs untrusted model output, so several mitigations are built in:

- **Agent tools can read/write files and run shell commands within the workspace.** File
  writes, diffs (single and atomic multi-file), deletions, renames, and shell commands are
  gated by an approval prompt and confined to the workspace root (`AssertUnderRoot`). Edit
  prompts show the actual diff before you confirm.
- **Permission rules** classify each call *before* the prompt: `allow` auto-approves, `deny`
  blocks, no match prompts (per-machine setting + committable `.inferpal/permissions.json`,
  first match wins). A built-in **hard denylist** of catastrophic shell commands
  (recursive root deletes, disk formatting, fork bombs, …) is always enforced — even with
  security alerts disabled, and including commands sourced from `.inferpal/validators.json`.
  Blocked calls are recorded in `/diagnostics`.
- **Anything the repository authored is force-prompted.** `.inferpal/validators.json` lets a
  project define the build command Smart Fix runs by itself after a write — and that file
  ships with every clone. Such a command is therefore never run unattended: it goes through
  the approval prompt with **no** auto-approval path (no `allow` rule, no session grant, not
  even *Disable security alerts*), asked once per session per command, and refused outright
  if no approval surface is available. The built-in .NET / TypeScript / Rust / Go validators
  are ours, not the repository's, and keep running silently. The consent you give your own
  agent is not consent to a stranger's command.
- **What a repository may decide for you is enumerated, not filtered.** The committable project
  profile (`.inferpal/project.json`) describes preferences and grants nothing. One key is applied
  — `indexExclude`, and only additively: a clone can keep files out of the semantic index, never
  put more in, and nothing is hidden from the file tools. Model roles and context size are shown
  by `/onboard` and applied only by an explicit `/onboard apply`, because they are machine
  choices. Everything else is ignored, including keys nested under `recommend` to look harmless;
  the ones that would execute, authorise or redirect (`validators`, `permissions`, `baseUrl`,
  `apiKey`, …) are named in the report. The classification is an **allow-list**: an unknown key is
  ignored rather than interpreted, so the guarantee does not depend on us having predicted the
  dangerous names.
- **Indirect execution always reaches you.** The denylist matches text, and text matching
  cannot see through indirection (`$c = '…'; iex $c`, `-EncodedCommand`, `FromBase64String`,
  runtime script blocks, `& $var`). Rather than pretend otherwise, those constructs are
  **force-prompted**: no `allow` rule, no session grant and not even *Disable security alerts*
  can auto-approve them — a human reads the raw command first. Treat the denylist as an
  accident guard; the approval prompt is the boundary.
- **`fetch_url` and `web_search` are approval-gated** (they are the outbound channel of the
  *lethal trifecta*) and outbound fetches pass a hardened SSRF guard (blocks DNS rebinding,
  IPv4-mapped IPv6, `0.0.0.0/8`, loopback/private ranges, with a ReDoS-safe timeout).
- **MCP servers run arbitrary external code** you choose to enable; each MCP tool call is
  approval-gated, and "always allow" grants are session-scoped and never persisted.
- **Secrets** — MCP OAuth tokens are encrypted at rest: DPAPI on Windows, the editor's secret
  storage (OS keychain) when running under VS Code on Linux/macOS.
- **Undo**: every changed file is snapshotted; `/undo-run` reverts a whole agent run
  (restores edited files, deletes files created that run).

If you enable **Disable security alerts**, the calls that would otherwise prompt are
auto-approved — use only in trusted contexts. The built-in catastrophic-command denylist and
any `deny` rules still apply.

See [docs/tools.md](docs/tools.md) and [docs/architecture.md](docs/architecture.md) for details.

## Known accepted advisories

| Advisory | Component | Status |
|---|---|---|
| [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (CVE-2025-6965) | `SQLitePCLRaw.lib.e_sqlite3` (SQLite < 3.50.2) | **Suppressed** — no fixed bundle is published yet (all versions ≤ 2.1.11 are affected). The vulnerable path requires malicious SQL or an untrusted database file; Inferpal opens only its own local index databases, builds every query itself, and never enables SQLite extension loading. Tracked in `Inferpal.csproj`; will be removed when a patched bundle ships. |
