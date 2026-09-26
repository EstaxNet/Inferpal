using System.IO;
using System.Text.Json;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>insert_at_cursor</c> and <c>replace_selection</c> write the model's text into the document the user
/// is looking at — and the model writes LF. Visual Studio's editor inserts a text exactly as given, so a
/// CRLF file (its default on Windows) got LF lines in the middle: the mixed endings that
/// <c>apply_diff</c>, <c>write_file</c> and the code actions all convert away. The text reaches the editor
/// in the document's own endings, through the gate both tools share.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class EditorWriteLineEndingsTests
{
    private sealed class RecordingEditor(string documentText) : IEditorSurface
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inferpal-tests-eol-active.cs");
        public string? Received { get; private set; }

        public bool IsAvailable => true;
        public string? ActiveDocumentPath => Path;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [Path];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(Path, documentText));

        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct)
        {
            Received = text;
            return Task.FromResult<string?>(Path);
        }

        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct)
        {
            Received = text;
            return Task.FromResult<EditorEditResult?>(new EditorEditResult(Path, ReplacedSelection: true));
        }

        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Yes : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, Services.CodeActions.DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private const string CrlfDocument = "class A\r\n{\r\n    void M() { }\r\n}\r\n";
    private const string ModelText    = "void N()\n{\n    M();\n}\n";

    private static async Task<string?> Run(bool replace, string documentText, string text)
    {
        var editor = new RecordingEditor(documentText);
        ITool tool = replace
            ? new ReplaceSelectionTool(editor, new Yes(), new FileHistoryService())
            : new InsertAtCursorTool(editor, new Yes(), new FileHistoryService());
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { text }));
        await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return editor.Received;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheModelsText_ReachesACrlfDocument_InCrlf(bool replace)
    {
        var received = await Run(replace, CrlfDocument, ModelText);

        Assert.Equal("void N()\r\n{\r\n    M();\r\n}\r\n", received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnLfDocument_GetsTheTextAsGiven(bool replace)
    {
        Assert.Equal(ModelText, await Run(replace, CrlfDocument.Replace("\r\n", "\n"), ModelText));
    }

    [Fact]
    public async Task ADocumentWithNoLineBreak_SaysNothingAboutItsConvention_AndTheTextIsKept()
    {
        Assert.Equal("a\r\nb", await Run(replace: false, "single line", "a\r\nb"));
        Assert.Equal(ModelText, await Run(replace: true, string.Empty, ModelText));
    }
}
