using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the writing and exploring tools guarantee when the model's input or the disk do not
/// cooperate: the sandbox holds, a write without its safety net does not happen, a batch fails as a
/// whole, and the human sees what they approve.
/// </summary>
public sealed class ToolSafetyRegressionTests : IDisposable
{
    private readonly string _base;
    private readonly string _ws;

    public ToolSafetyRegressionTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "inferpal-toolsafety-" + Guid.NewGuid().ToString("N"));
        _ws   = Path.Combine(_base, "ws");
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_base, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private sealed class CapturingApproval(bool approve = true) : IApprovalService
    {
        public string? LastDetails { get; private set; }
        public int Calls { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Calls++;
            LastDetails = details;
            return Task.FromResult(approve);
        }
    }

    private static JsonElement Args(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static async Task<string> RunAsync(ITool tool, object args)
    {
        try { return await tool.ExecuteAsync(Args(args), CancellationToken.None); }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Makes the snapshot of any file under the workspace fail: a FILE where the history
    /// folder must be created.</summary>
    private void BlockTheHistoryFolder()
    {
        Directory.CreateDirectory(Path.Combine(_ws, ".inferpal"));
        File.WriteAllText(Path.Combine(_ws, ".inferpal", "history"), "not a folder");
    }

    // ── T1 — a file pattern never leaves the workspace ─────────────────────────

    /// <summary>
    /// .NET glues the folder part of a pattern onto the start folder without refusing <c>..</c>: only
    /// <c>path</c> went through the sandbox, and <c>file_pattern: "..\*"</c> read above it.
    /// </summary>
    [Fact]
    public async Task SearchInFiles_APatternWithParentSegments_DoesNotReadOutsideTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_base, "outside.txt"), "SECRET-TOKEN");
        File.WriteAllText(Path.Combine(_ws, "inside.txt"), "nothing here");

        var result = await RunAsync(new SearchInFilesTool(() => _ws),
            new { path = _ws, pattern = "SECRET-TOKEN", file_pattern = Path.Combine("..", "*.txt") });

        Assert.DoesNotContain("SECRET-TOKEN", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFiles_APatternWithParentSegments_DoesNotListOutsideTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_base, "outside.txt"), "x");

        var result = await RunAsync(new ListFilesTool(() => _ws),
            new { path = _ws, pattern = Path.Combine("..", "*.txt") });

        Assert.DoesNotContain("outside.txt", result, StringComparison.Ordinal);
    }

    /// <summary>The worst of the three: <c>rename_symbol</c> WRITES the files the pattern points at.</summary>
    [Fact]
    public async Task RenameSymbol_APatternWithParentSegments_DoesNotRewriteOutsideTheWorkspace()
    {
        var outside = Path.Combine(_base, "outside.ts");
        File.WriteAllText(outside, "let fooBar = 1;\n");
        File.WriteAllText(Path.Combine(_ws, "inside.ts"), "let other = 2;\n");

        await RunAsync(new RenameSymbolTool(new CapturingApproval(), new FileHistoryService(), () => _ws),
            new { root = _ws, old_name = "fooBar", new_name = "bazQux", file_pattern = Path.Combine("..", "*.ts"), dry_run = false });

        Assert.Equal("let fooBar = 1;\n", File.ReadAllText(outside));
    }

    /// <summary>Witness: a model's reflex <c>**/*.txt</c> is still a valid pattern (the walk is already recursive).</summary>
    [Fact]
    public async Task ListFiles_ARecursiveGlobPrefix_StillListsTheWorkspaceFiles()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        File.WriteAllText(Path.Combine(_ws, "sub", "inside.txt"), "x");

        var result = await RunAsync(new ListFilesTool(() => _ws), new { path = _ws, pattern = "**/*.txt" });

        Assert.Contains("inside.txt", result, StringComparison.Ordinal);
    }

    // ── T3 — no write without the backup the tool promises ─────────────────────

    /// <summary>
    /// The description promises that <c>restore_file</c> undoes a deletion. When the backup failed
    /// (history folder not writable, disk full), the file was deleted anyway — for good.
    /// </summary>
    [Fact]
    public async Task DeleteFile_WhenNoBackupCanBeSaved_LeavesTheFileInPlace()
    {
        var path = Path.Combine(_ws, "a.txt");
        File.WriteAllText(path, "precious");
        BlockTheHistoryFolder();

        var result = await RunAsync(new DeleteFileTool(new CapturingApproval(), new FileHistoryService(), () => _ws),
                                    new { path });

        Assert.True(File.Exists(path), result);
    }

    [Fact]
    public async Task WriteFile_WhenNoBackupCanBeSaved_DoesNotOverwrite()
    {
        var path = Path.Combine(_ws, "a.txt");
        File.WriteAllText(path, "precious");
        BlockTheHistoryFolder();

        var result = await RunAsync(new WriteFileTool(new CapturingApproval(), new FileHistoryService(), () => _ws),
                                    new { path, content = "replaced" });

        Assert.Equal("precious", File.ReadAllText(path));
    }

    /// <summary>Witness: a NEW file has nothing to back up, so it is written.</summary>
    [Fact]
    public async Task WriteFile_ANewFile_IsWrittenEvenWithoutAHistoryFolder()
    {
        var path = Path.Combine(_ws, "new.txt");
        BlockTheHistoryFolder();

        await RunAsync(new WriteFileTool(new CapturingApproval(), new FileHistoryService(), () => _ws),
                       new { path, content = "fresh" });

        Assert.Equal("fresh", File.ReadAllText(path));
    }

    // ── T8 — apply_edits is all-or-nothing on disk too ─────────────────────────

    /// <summary>
    /// The description promises "if ANY edit fails, NO file changes". A WRITE failure (read-only or
    /// locked file) left the files written before it modified.
    /// </summary>
    [Fact]
    public async Task ApplyEdits_AWriteThatFailsMidBatch_RestoresTheFilesAlreadyWritten()
    {
        var a = Path.Combine(_ws, "a.txt");
        var b = Path.Combine(_ws, "b.txt");
        File.WriteAllText(a, "alpha");
        File.WriteAllText(b, "beta");
        File.SetAttributes(b, FileAttributes.ReadOnly);

        var tool = new ApplyEditsTool(new CapturingApproval(), new FileHistoryService(), () => _ws, smartFix: null);
        var result = await RunAsync(tool, new
        {
            edits = new[]
            {
                new { path = a, old_content = "alpha", new_content = "ALPHA" },
                new { path = b, old_content = "beta",  new_content = "BETA"  },
            },
        });

        Assert.Equal("alpha", File.ReadAllText(a));
        Assert.Contains("b.txt", result, StringComparison.Ordinal);
    }

    // ── T2 — compiler spans over a file edited since indexing ──────────────────

    /// <summary>
    /// The C# index's positions are those of the file as it read it. A file edited since then (the
    /// index refreshes after a delay, and never with RAG off) got the rename at the OLD positions:
    /// corrupted code, written to disk.
    /// </summary>
    [Fact]
    public void RenameSpans_OverAFileEditedSinceIndexing_AreRefused_NotAppliedAtTheOldOffsets()
    {
        Microsoft.CodeAnalysis.Text.TextSpan[] spans = [new(6, 3)];   // "Foo" in "class Foo {}"

        var fresh = RenameSymbolTool.ApplySpans("class Foo {}", spans, "Foo", "Bar");
        Assert.Equal(("class Bar {}", 1, false), fresh);               // witness: up-to-date spans

        var edited = RenameSymbolTool.ApplySpans("// x\nclass Foo {}", spans, "Foo", "Bar");
        Assert.True(edited.Stale);
        Assert.Equal("// x\nclass Foo {}", edited.NewContent);
    }

    // ── T11 — the human sees what the rename changes ───────────────────────────

    /// <summary>
    /// The <c>rename_symbol</c> approval prompt showed only a count ("N file(s)"): the security
    /// boundary is the human reading — and they had nothing to read.
    /// </summary>
    [Fact]
    public async Task RenameSymbol_TheApprovalPromptShowsTheChangedLines()
    {
        File.WriteAllText(Path.Combine(_ws, "app.ts"), "let fooBar = 1; // marker-line\n");
        var approval = new CapturingApproval(approve: false);

        await RunAsync(new RenameSymbolTool(approval, new FileHistoryService(), () => _ws),
            new { root = _ws, old_name = "fooBar", new_name = "bazQux", dry_run = false });

        Assert.Equal(1, approval.Calls);
        Assert.Contains("marker-line", approval.LastDetails, StringComparison.Ordinal);
    }
}
