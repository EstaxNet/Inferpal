# Development

How to build, test, extend, and contribute to Inferpal. For how the pieces fit together see
**[Architecture](architecture.md)**.

## Prerequisites

- **.NET 8 SDK**
- **Visual Studio 2022 (17.9+) or 2026 (18.x)** with the Visual Studio extension development
  workload
- A running model server for manual testing — see [Getting Started](getting-started.md)

## Build

```powershell
# Debug — includes the PDB in the VSIX (Attach to Process)
dotnet build Inferpal/Inferpal.csproj

# Release — optimized, no symbols, warnings-as-errors
dotnet build Inferpal/Inferpal.csproj -c Release
```

The VSIX is produced under `Inferpal\bin\Debug\net8.0-windows\` (or `Release\`).

### Deploy to the experimental hive

```powershell
./deploy-debug.ps1      # builds Debug and installs into the VS experimental hive
./deploy-release.ps1    # Release variant
```

The script auto-detects the machine-specific experimental hive id — never hard-code it.

## Tests

```powershell
dotnet test Inferpal.Tests/Inferpal.Tests.csproj
```

The suite has **662 xUnit tests**. The test project uses `InternalsVisibleTo`, so the
extension's `internal` types are testable directly. Most services are written as static,
side-effect-free helpers specifically so they can be unit-tested without Visual Studio (e.g.
`DiffComputer`, `ContextBudgetGauge`, `ThemePalette`, `ConnectionStatusPresenter`,
`PromptHistoryNavigator`, `ModelCatalog`, `RulesService`, `ChecksService`, `FixPromptBuilder`,
`HistoryCompaction`).

## Project layout

```
Inferpal/
├── Commands/        VS menu commands + editor context-menu code actions
├── Config/          InferpalConfig — all persisted settings
├── GhostText/       In-process MEF: inline completions, auto-scroll, VS event trackers
├── Localization/    Strings.resx (+ 9 locales) and the manual Strings.cs wrapper
├── Models/          Ollama / OpenAI DTOs
├── Services/        Providers, tools, RAG, @Docs, MCP, scheduling, parsing
│   ├── Tools/       The 26 built-in ITool implementations
│   ├── Rag/         CodeChunker, RagDatabase, ProjectIndexService
│   ├── Docs/        @Docs crawler/index
│   ├── Lsp/         LSP semantic-chunking tier
│   └── Mcp/         MCP stdio JSON-RPC client
└── ToolWindow/      RemoteUI view models, content, settings
Inferpal.Tests/      xUnit test project
```

## Adding a built-in tool

1. Create `Services/Tools/MyTool.cs` implementing `ITool`.
2. Use **English** for `Name`, `Description`, and `Parameters` (best model compatibility).
3. Use `Strings.X(...)` for any user-facing return text (localization).
4. Register it in `ToolRegistry.cs`: `Register(new MyTool())`.

```csharp
internal sealed class MyTool : ITool
{
    public string Name        => "my_tool";
    public string Description  => "Does something useful.";
    public object Parameters   => new { type = "object", properties = new { }, required = Array.Empty<string>() };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // implementation
        return "result";
    }
}
```

If the tool touches the filesystem, runs commands, or reaches the network, take an
`IApprovalService` and gate the action (see existing tools).

## Adding a language

1. Create `Localization/Strings.XX-YY.resx` with the **same keys** as `Strings.resx`.
2. Add the culture code and display name to `LanguageOptions` in `InferpalSettingsData.cs`.
3. Build — the satellite `XX-YY/Inferpal.resources.dll` is generated and included in the VSIX.

> [!IMPORTANT]
> `Strings.cs` is written **by hand** (not auto-generated). Every new `.resx` key must be
> added to `Strings.cs` **and** translated in all 9 localized `.resx` files at the same time.

## Coding constraints to know

These are the non-obvious rules that keep the Remote UI / out-of-process model working:

| Topic | Rule |
|---|---|
| Cross-boundary types | Only `[DataContract]` types with primitive members (and `ObservableCollection<T>` of such) cross to `devenv.exe`. |
| `xmlns` | Never write `assembly=Inferpal` in a XAML `xmlns` (→ MC3072). |
| Theme / data binding | Remote UI does not propagate via `ElementName` across nested `DataTemplate`s — push values down the VM hierarchy as `[DataMember]`. |
| Label initialization | Initial VM values aren't read on `DataContext` assignment — call `ApplyLabels()` in `ControlLoadedAsync`. |
| TwoWay collections | Never `.Clear()` a TwoWay-bound `ObservableCollection`; update in place. |
| Tools & VS context | Tools have no `IClientContext` — use `VsContextHolder`. |
| Release builds | `TreatWarningsAsErrors` is on; command strings must come from `string-resources.json` (`%key%`) or you hit `CEE0027`. |

See **[Architecture](architecture.md)** for the reasoning behind each.

## Contributing

Contributions are welcome. The short version: implement `ITool`, register it in
`ToolRegistry.cs`, and add any new strings to all 10 `.resx` files **and** to `Strings.cs`.
Keep new logic in testable, VS-free helpers where possible and add xUnit coverage.
