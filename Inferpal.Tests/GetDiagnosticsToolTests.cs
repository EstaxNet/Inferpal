using System.IO;
using System.Text.Json;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

public class GetDiagnosticsToolTests
{
    private sealed class FakeEditorSurface : IEditorSurface
    {
        public string? Diagnostics { get; set; }
        public int DiagnosticsCalls { get; private set; }

        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);

        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct)
        {
            DiagnosticsCalls++;
            return Task.FromResult(Diagnostics);
        }
    }

    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task LiveEditorDiagnostics_ReturnedInstantlyWithoutBuilding()
    {
        var editor = new FakeEditorSurface { Diagnostics = "a.cs(1,2): error CS0103: boom" };
        var tool   = new GetDiagnosticsTool(editor);

        var result = await tool.ExecuteAsync(Args(new { }), CancellationToken.None);

        Assert.Equal("a.cs(1,2): error CS0103: boom", result);
        Assert.Equal(1, editor.DiagnosticsCalls);
    }

    [Fact]
    public async Task ExplicitPath_BypassesEditorDiagnostics()
    {
        // An explicit path means "build THAT project" — the live-panel shortcut must not hijack it.
        var editor  = new FakeEditorSurface { Diagnostics = "a.cs(1,2): error CS0103: boom" };
        var tool    = new GetDiagnosticsTool(editor);
        var missing = Path.Combine(Path.GetTempPath(), "inferpal-does-not-exist", "x.csproj");

        var result = await tool.ExecuteAsync(Args(new { path = missing }), CancellationToken.None);

        Assert.Equal(0, editor.DiagnosticsCalls);
        Assert.NotEqual(editor.Diagnostics, result); // fell through to the file-not-found flow
    }
}
