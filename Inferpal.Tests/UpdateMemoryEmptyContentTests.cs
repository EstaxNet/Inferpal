using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  update_memory refuses an empty append or replace BEFORE the approval prompt.
//
//  The refusal came after the prompt and after the backup: the user approved a write that never
//  happened, and the backup counted a write in the run — what the end-of-turn checks read as an edit
//  that landed.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class UpdateMemoryEmptyContentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-memory-empty-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

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

    private sealed class Counting : IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    private static JsonElement Args(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    [Theory]
    [InlineData("append")]
    [InlineData("replace")]
    public async Task AnEmptyWrite_IsRefused_BeforeThePrompt_AndCountsNoWrite(string mode)
    {
        var history  = new FileHistoryService();
        var approval = new Counting();
        var tool     = new UpdateMemoryTool(new NoEditor(), approval, history, () => _root);

        string result;
        using (history.BeginRunScope())
        {
            result = await tool.ExecuteAsync(Args(new { mode, content = "  " }), CancellationToken.None);
            Assert.Equal(0, history.CurrentRunWriteCount);
        }

        Assert.Equal(Strings.UpdateMemoryNoContent, result);
        Assert.Equal(0, approval.Asked);
        Assert.False(File.Exists(Path.Combine(_root, ".inferpal", "memory.md")));
    }

    [Fact]
    public async Task AWriteWithContent_IsAskedAbout_AndLands()
    {
        // Reference arm: the guard refuses emptiness only.
        var history  = new FileHistoryService();
        var approval = new Counting();
        var tool     = new UpdateMemoryTool(new NoEditor(), approval, history, () => _root);

        using (history.BeginRunScope())
            await tool.ExecuteAsync(Args(new { mode = "append", content = "Use tabs." }), CancellationToken.None);

        Assert.Equal(1, approval.Asked);
        Assert.Contains("Use tabs.", File.ReadAllText(Path.Combine(_root, ".inferpal", "memory.md")));
    }
}
