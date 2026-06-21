# Contributing to Inferpal

Contributions are welcome! This file is a quick pointer — the full guide lives in
**[docs/development.md](docs/development.md)** (build, test, project layout, coding constraints).

## Quick start

```powershell
dotnet build Inferpal/Inferpal.csproj -c Release   # warnings-as-errors
dotnet test  Inferpal.Tests/Inferpal.Tests.csproj
```

If you already have the extension installed, push code changes into it without a full reinstall
with [`deploy-debug.ps1`](docs/development.md#fast-redeploy-with-deploy-debugps1).

## Before opening a PR

- Keep new logic in **testable, VS-free helpers** and add xUnit coverage where it makes sense.
- New filesystem / command / network actions must be **approval-gated** and **workspace-confined**.
- Any new user-facing string goes into **all** `.resx` locales **and** into the hand-written
  `Strings.cs`.
- Update the relevant page under `docs/` when behavior or configuration changes.

Adding a tool or a language? See
[docs/development.md → Adding a built-in tool](docs/development.md#adding-a-built-in-tool) and
[→ Adding a language](docs/development.md#adding-a-language).

By contributing, you agree your work is licensed under the project's
[GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0).
