using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_active_document</c> promised "the full content" and returned it whole: past the context cap the loop cut a
/// long file in its MIDDLE, under a marker that named no way to read the cut part. It pages like <c>read_file</c>.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares a localized header
public class ActiveDocumentPagingTests
{
    private const string FilePath = @"C:\repo\src\Big.cs";

    private static async Task<string> RunAsync(string text)
    {
        var tool = new GetActiveDocumentTool(new OneDocumentEditor(text));
        using var args = JsonDocument.Parse("{}");
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task ALongActiveFile_ComesBackAsItsFirstPage_NamingWhereReadFileReadsOn()
    {
        var text = string.Concat(Enumerable.Range(1, 2000).Select(i => $"    var line{i} = Compute({i}); // body\n"));

        var answer = await RunAsync(text);

        Assert.StartsWith(Strings.ActiveDocResult(FilePath, string.Empty).TrimEnd(), answer);          // witness
        Assert.Contains("var line1 = Compute(1);", answer);
        Assert.Matches(@"call read_file with start_line=\d+ to read on", answer);
        // The whole answer enters the context as it is: nothing cut in its middle, unannounced.
        Assert.Equal(answer, AgentOrchestrator.CapForContext(answer));
    }

    [Fact]
    public async Task AShortActiveFile_IsReturnedWhole()
    {
        var answer = await RunAsync("class Small { }\n");

        Assert.Equal(Strings.ActiveDocResult(FilePath, "class Small { }\n"), answer);
    }

    private sealed class OneDocumentEditor(string text) : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => FilePath;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [FilePath];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(new ActiveDocument(FilePath, text));
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
