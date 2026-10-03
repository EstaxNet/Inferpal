using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A build or an editor reports its diagnostics in its own order, errors among the warnings — and hundreds of nullable
/// or lint warnings is an ordinary project. Past a cap (the editor's 200 lines, the context's 8 000 characters) the
/// summary still said "2 errors" while the errors were the lines cut: the model fixed warnings, or concluded the
/// errors were gone. Errors are listed first, and what is left out is counted per severity.
/// </summary>
public class DiagnosticsErrorsFirstTests
{
    /// <summary>300 warnings, then the 2 errors — the order a build of a warning-heavy project gives.</summary>
    private static List<string> WarningsThenErrors(string prefix)
    {
        var lines = Enumerable.Range(0, 300)
            .Select(i => $"{prefix}src/Legacy{i}.cs({i + 1},5): warning CS8618: Non-nullable property 'Name{i}' must contain a value")
            .ToList();
        lines.Add($"{prefix}src/Cart.cs(12,9): error CS0103: The name 'totl' does not exist in the current context");
        lines.Add($"{prefix}src/Order.cs(40,1): error CS1002: ; expected");
        return lines;
    }

    [Fact]
    public void ABuild_ListsItsErrorsFirst_AndTheyReachTheModelPastTheContextCap()
    {
        var output = string.Join("\n", WarningsThenErrors(@"C:\repo\")) + "\n";
        var run    = new ChildProcessResult(1, output, string.Empty, TimedOut: false);

        var answer = GetDiagnosticsTool.Interpret(run, "app.csproj", 90);
        var model  = AgentOrchestrator.CapForContext(answer);

        Assert.Contains("CS8618", answer);                                                            // witness: parsed
        Assert.True(answer.IndexOf("error CS0103", StringComparison.Ordinal)
                    < answer.IndexOf("warning CS8618", StringComparison.Ordinal), "errors are not listed first");
        Assert.Contains("error CS0103", model);
        Assert.Contains("error CS1002", model);
        Assert.Contains("102 more warning(s) not listed", answer);
    }

    [Fact]
    public async Task TheEditorPanel_ListsItsErrorsFirst()
    {
        var editor = new PanelEditor(string.Join("\n", WarningsThenErrors("")));
        var tool   = new GetDiagnosticsTool(editor);

        using var args = JsonDocument.Parse("{}");
        var answer = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        var firstDiagnostic = answer.Split('\n').First(l => l.Contains("): ", StringComparison.Ordinal));
        Assert.Contains("error CS0103", firstDiagnostic);
        Assert.Contains("error CS1002", answer);
        Assert.Contains("102 more warning(s) not listed", answer);
    }

    [Fact]
    public void AShortList_IsListedWhole_WithoutANote()
    {
        var listed = GetDiagnosticsTool.ErrorsFirst([
            "a.cs(1,1): warning CS0168: unused",
            "b.cs(2,2): error CS0103: missing",
            "… an editor note",
        ]);

        Assert.Equal("b.cs(2,2): error CS0103: missing\na.cs(1,1): warning CS0168: unused\n… an editor note", listed);
    }

    // ── VS Code: the extension caps its panel at 200 lines; no test runner there, held on its source ──

    [Fact]
    public void VsCode_ThePanelIsCappedAfterTheErrorsAreSetFirst()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "vscode", "src", "editorBridge.ts")));

        var start = code.IndexOf("async editorDiagnostics()", StringComparison.Ordinal);
        Assert.True(start >= 0, "editorDiagnostics is not where it was");                                // WITNESS
        var body = code[start..code.IndexOf("activeEditor()", start, StringComparison.Ordinal)];

        Assert.Contains("const shownErrors = errors.slice(0, MAX_LINES);", body);
        Assert.Contains("[...shownErrors, ...shownWarnings]", body);
        Assert.Contains("more error(s)", body);
    }

    private sealed class PanelEditor(string diagnostics) : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(diagnostics);
    }
}
