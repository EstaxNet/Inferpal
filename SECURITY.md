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

## Design notes relevant to security

Inferpal is local-first and runs untrusted model output, so several mitigations are built in:

- **Workspace sandbox** — every path-taking tool goes through a single `AssertUnderRoot` guard.
- **Approval gates** — file writes, deletes, shell commands, `fetch_url`, `web_search`, custom
  shell tools, and MCP calls each require explicit approval; a non-bypassable denylist blocks
  catastrophic shell commands.
- **Hardened SSRF guard** on outbound fetches (DNS rebinding, IPv4-mapped IPv6, loopback/private
  ranges) with a ReDoS-safe timeout.
- **Secrets** — MCP OAuth tokens are encrypted at rest with DPAPI.

See [docs/tools.md](docs/tools.md) and [docs/architecture.md](docs/architecture.md) for details.

## Known accepted advisories

| Advisory | Component | Status |
|---|---|---|
| [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (CVE-2025-6965) | `SQLitePCLRaw.lib.e_sqlite3` (SQLite < 3.50.2) | **Suppressed** — no fixed bundle is published yet (all versions ≤ 2.1.11 are affected). The vulnerable path requires malicious SQL or an untrusted database file; Inferpal opens only its own local index databases, builds every query itself, and never enables SQLite extension loading. Tracked in `Inferpal.csproj`; will be removed when a patched bundle ships. |
