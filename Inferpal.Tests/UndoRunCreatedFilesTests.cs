using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/undo-run</c> deletes what the run <b>created</b> — provided somebody told it so.
/// <c>NoteCreated</c> n'avait qu'UN appelant.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A file created during a run has no snapshot <i>by construction</i>: <c>SnapshotAsync</c>
/// returns before recording anything when the file does not exist. It therefore enters
/// <c>/undo-run</c>'s perimeter only through <c>NoteCreated</c>, and <c>write_file</c> was its only
/// caller. <c>update_memory</c> creates <c>.inferpal/memory.md</c> on its first call: undoing the run
/// gave every other file back and <b>left that one</b> — the one re-injected into the system prompt
/// of every later session. The run is "undone" while its most durable effect stays.
/// </para>
/// <para>
/// ⚠ <b>The property moves up into the funnel instead of staying a list.</b>
/// <c>BackUpBeforeChangeAsync</c> <i>already knows</i> the file does not exist — that is exactly its
/// <c>(true, "")</c> branch — so that is where "the write that follows CREATES this file" is said,
/// once, for its eight callers. <c>write_file</c>'s manual call becomes a harmless duplicate:
/// <c>RecordFirst</c> keeps only the first change per path.
/// </para>
/// </remarks>
public class UndoRunCreatedFilesTests
{
    private sealed class NoEditor : IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class YesApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Json(string s) => JsonSerializer.Serialize(s);

    [Fact]
    public async Task UndoingARun_DeletesTheProjectMemoryThatRunCreated()
    {
        var root    = Directory.CreateTempSubdirectory("inferpal-undo").FullName;
        var memPath = Path.Combine(root, ".inferpal", "memory.md");
        try
        {
            var history = new FileHistoryService();
            var tool    = new UpdateMemoryTool(new NoEditor(), new YesApproval(), history, () => root);

            using (history.BeginRunScope())
                await tool.ExecuteAsync(
                    Raw("""{"mode":"append","content":"the agent decided this"}"""), CancellationToken.None);

            // WITNESS: the run really did create the file — without that, the test would measure a
            // deletion with nothing to delete.
            Assert.True(File.Exists(memPath), "update_memory did not create the memory: nothing to undo.");

            var run = history.Runs[0];
            await history.UndoRunAsync(run, CancellationToken.None);

            Assert.False(File.Exists(memPath),
                         "the memory written by the run survives /undo-run, and goes back into every later prompt.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UndoingARun_StillDeletesWhatWriteFileCreated()
    {
        // Reference arm: the one site that already held the property still holds it.
        var root = Directory.CreateTempSubdirectory("inferpal-undo").FullName;
        var made = Path.Combine(root, "New.cs");
        try
        {
            var history = new FileHistoryService();
            var tool    = new WriteFileTool(new YesApproval(), history, () => root);

            using (history.BeginRunScope())
                await tool.ExecuteAsync(
                    Raw($$"""{"path":{{Json(made)}},"content":"class New {}"}"""), CancellationToken.None);

            Assert.True(File.Exists(made));
            await history.UndoRunAsync(history.Runs[0], CancellationToken.None);
            Assert.False(File.Exists(made));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UndoingARun_RestoresAModifiedFileInsteadOfDeletingIt()
    {
        // ⚠ Reference arm for the DISCRIMINATOR: "created" and "modified" are not undone the same
        // way, and confusing the two would destroy the user's work instead of giving it back.
        var root = Directory.CreateTempSubdirectory("inferpal-undo").FullName;
        var kept = Path.Combine(root, "Old.cs");
        await File.WriteAllTextAsync(kept, "class Old { }\n");
        try
        {
            var history = new FileHistoryService();
            var tool    = new WriteFileTool(new YesApproval(), history, () => root);

            using (history.BeginRunScope())
                await tool.ExecuteAsync(
                    Raw($$"""{"path":{{Json(kept)}},"content":"class Rewritten {}"}"""), CancellationToken.None);

            await history.UndoRunAsync(history.Runs[0], CancellationToken.None);

            Assert.True(File.Exists(kept), "a MODIFIED file is not deleted on undo.");
            Assert.Equal("class Old { }\n", await File.ReadAllTextAsync(kept));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TheFunnelItself_RecordsTheCreation()
    {
        // The property is carried by BackUpBeforeChangeAsync, not by its callers: that is what
        // makes the next tool that creates a file inherit it.
        var root = Directory.CreateTempSubdirectory("inferpal-undo").FullName;
        var absent = Path.Combine(root, "NotYet.txt");
        try
        {
            var history = new FileHistoryService();
            using (history.BeginRunScope())
            {
                var (saved, snapshot) = await history.BackUpBeforeChangeAsync(absent, CancellationToken.None);

                // An absent file has nothing to back up: that is NOT a backup failure.
                Assert.True(saved);
                Assert.Equal(string.Empty, snapshot);
            }

            var change = Assert.Single(history.Runs[0].Changes);
            Assert.Equal(absent, change.OriginalPath);
            Assert.Null(change.SnapshotPath);        // created: undo deletes
            Assert.False(change.SnapshotFailed);     // and emphatically not "backup failed"
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
