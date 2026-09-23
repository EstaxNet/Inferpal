using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A Problems panel that carries no ERROR cannot answer "does it compile?" — warnings-only is the
/// ordinary state of a workspace (a lint rule, a nullable warning in an open file), and served in
/// place of the build it made <c>get_diagnostics</c> — <c>/build</c> included — never compile in VS
/// Code, without saying so.
/// </summary>
public class WarningsOnlyPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-warnpanel-" + Guid.NewGuid().ToString("N"));

    public WarningsOnlyPanelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class PanelEditor(string? panel) : IEditorSurface
    {
        public int Calls { get; private set; }
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) { Calls++; return Task.FromResult(panel); }
    }

    // The exact line shape vscode/src/editorBridge.ts writes: `rel(line,col): sev source code: message`.
    private const string LintWarning  = "src/a.ts(3,7): warning eslint no-unused-vars: 'x' is assigned a value but never used.";
    private const string CompileError = "src/a.ts(1,1): error ts 2304: Cannot find name 'foo'.";

    /// <summary>A project MSBuild rejects in a fraction of a second, with an error line that proves a build ran.</summary>
    private void PlantProject() => File.WriteAllText(Path.Combine(_root, "x.csproj"), "<Project></Project>");

    private Task<string> Run(PanelEditor editor) =>
        new GetDiagnosticsTool(editor, () => _root)
            .ExecuteAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

    [Fact]
    public async Task WarningsOnlyPanel_WithAProject_Compiles()
    {
        PlantProject();
        var editor = new PanelEditor(LintWarning);

        var result = await Run(editor);

        Assert.Equal(1, editor.Calls);          // witness: the panel WAS consulted
        Assert.Contains("MSB4040", result);      // and the build ran anyway
        Assert.DoesNotContain(Strings.DiagFromEditor, result);
    }

    [Fact]
    public async Task WarningsOnlyPanel_WithNothingToCompile_IsServed_AndSaysNoBuildRan()
    {
        // A TypeScript workspace: no project to build, so the panel is the only answer there is —
        // "No .sln or .csproj found" would throw away the one thing the editor knows.
        var result = await Run(new PanelEditor(LintWarning));

        Assert.StartsWith(Strings.DiagFromEditor, result);
        Assert.Contains(LintWarning, result);
        Assert.DoesNotContain(Strings.DiagNoProject, result);
    }

    [Fact]
    public async Task PanelWithAnError_IsServedWithoutBuilding_AndSaysSo()
    {
        // Reference arm: the fast path keeps its reason to exist — a real error, answered instantly.
        PlantProject();
        var result = await Run(new PanelEditor(LintWarning + "\n" + CompileError));

        Assert.DoesNotContain("MSB4040", result);
        Assert.StartsWith(Strings.DiagFromEditor, result);
        Assert.Contains(CompileError, result);
    }

    [Fact]
    public async Task CleanPanel_Compiles()
    {
        // Reference arm: an empty panel proves nothing about unopened files — unchanged.
        PlantProject();
        var result = await Run(new PanelEditor(null));

        Assert.Contains("MSB4040", result);
        Assert.DoesNotContain(Strings.DiagFromEditor, result);
    }

    [Theory]
    [InlineData("a.ts(1,1): error ts 2304: Cannot find name 'foo'.", true)]
    [InlineData("src/x.ts(12,3): error eslint @typescript-eslint/no-explicit-any: Unexpected any.", true)]
    [InlineData("Foo.cs(4,9): error csharp CS0103: The name 'x' does not exist.", true)]
    [InlineData("a.ts(3,7): warning eslint no-unused-vars: 'x' is never used.", false)]
    [InlineData("README.md(1,1): warning markdownlint MD041: First line should be a heading.", false)]
    public void PanelErrorLines_AreRecognisedInTheShapeTheEditorWrites(string line, bool isError) =>
        Assert.Equal(isError, GetDiagnosticsTool.PanelReportsErrors(line));
}
