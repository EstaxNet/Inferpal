# Contributing to Inferpal

Thanks for your interest in improving Inferpal! This is a Visual Studio extension written in
C# (.NET 8, and .NET 10 for the VS Code host). The full technical reference lives in **[docs/development.md](docs/development.md)**.

## Getting set up

- **.NET 10 SDK**, and the **.NET 8 Desktop Runtime** (the test suite's Windows leg runs on it)
- **Visual Studio 2026** with the extension-development workload (the development scripts target its
  experimental instance). The VSIX itself installs in Visual Studio 2026 and in Visual Studio 2022 from
  17.14 — one package: a change to the in-process half or to the extension's entry point has to hold in both.
- A local model server (Ollama / LM Studio / OpenAI-compatible) for manual testing

```powershell
# Build
dotnet build Inferpal/Inferpal.csproj            # Debug
dotnet build Inferpal/Inferpal.csproj -c Release # Release (warnings-as-errors)

# Test (1300 xUnit tests)
dotnet test Inferpal.Tests/Inferpal.Tests.csproj
```

## Before you open a pull request

- Branch off `main`; keep the change focused.
- **`dotnet build -c Release` must pass** (warnings are errors in Release).
- **All tests must stay green.** Add xUnit coverage for new logic — prefer extracting it into
  a static, VS-free helper so it can be tested without Visual Studio.
- Match the surrounding code style and comment density.

## Common contribution recipes

### Add a built-in tool
1. Create `Services/Tools/MyTool.cs` implementing `ITool`.
2. Use **English** for `Name`, `Description`, and `Parameters`.
3. Use `Strings.X(...)` for user-facing return text.
4. Register it in `ToolRegistry.cs`. Gate filesystem/shell/network actions behind
   `IApprovalService`.

### Add or change a UI string
`Strings.cs` is maintained **by hand**. Every new `.resx` key must be added to `Strings.cs`
**and** translated in all 9 localized `.resx` files at the same time (the suite checks key
parity across the 10 locales).

### Add a language
Create `Localization/Strings.XX-YY.resx` with the same keys, then add the culture to
`LanguageOptions` in `InferpalSettingsData.cs`.

## Architecture constraints

Inferpal uses the out-of-process VS Extensibility model with an in-process MEF package for
ghost text. A few non-obvious rules (Remote UI `[DataContract]` boundary, no `ElementName`
across templates, etc.) are documented in
**[docs/architecture.md](docs/architecture.md)** and
**[docs/development.md#coding-constraints-to-know](docs/development.md)** — please skim them
before touching the tool window or settings.

## License

By contributing, you agree that your contributions are licensed under the project's
[GNU GPL v3](LICENSE).
