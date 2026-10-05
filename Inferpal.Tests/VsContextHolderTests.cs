using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Inferpal.Services.VsIntegration;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Visual Studio: the active document is the one the user works in.
//
//  The agent's editor tools resolved the active view from the client context frozen by the latest
//  Inferpal command — the file the chat was opened from, at that version, with that selection — while
//  the chat window read the view that took the latest edit. And a closed active document stayed
//  active: attached, named by the welcome screen, edited by the agent.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class VsContextHolderTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "ws", "A.cs");
    private static readonly string B = Path.Combine(Path.GetTempPath(), "ws", "B.cs");

    // The SDK's snapshots cannot be faked (an internal member): the views are recorded by path, which is what decides.
    private static (VsContextHolder Holder, List<string> Changes) Opened(params string[] paths)
    {
        var holder  = new VsContextHolder();
        var changes = new List<string>();
        holder.ActiveFileChanged += (_, p) => changes.Add(p);
        foreach (var p in paths)
        {
            holder.RegisterOpen(p);
            holder.Activate(null, p);
        }
        return (holder, changes);
    }

    [Fact]
    public void ClosingTheActiveDocument_LeavesNoActiveDocument_AndSaysSo()
    {
        var (holder, changes) = Opened(A, B);   // B is active

        holder.ViewClosed(B);

        Assert.Equal(string.Empty, holder.ActiveFilePath);
        Assert.Equal(string.Empty, changes[^1]);
        Assert.True(holder.IsKnownClosed(B));
        Assert.Equal([A], holder.GetOpenPaths());
    }

    [Fact]
    public void ClosingAnotherDocument_KeepsTheActiveOne()
    {
        // Reference arm.
        var (holder, changes) = Opened(A, B);
        var count = changes.Count;

        holder.ViewClosed(A);

        Assert.Equal(B, holder.ActiveFilePath);
        Assert.Equal(count, changes.Count);
    }

    [Fact]
    public void ClosingOneOfTwoViewsOfTheActiveDocument_KeepsIt()
    {
        var (holder, _) = Opened(A, B);
        holder.RegisterOpen(B);   // a second view (split window)

        holder.ViewClosed(B);

        Assert.Equal(B, holder.ActiveFilePath);
        Assert.False(holder.IsKnownClosed(B));
    }

    [Fact]
    public void ADocumentOpenedAgain_IsNoLongerKnownClosed()
    {
        var (holder, _) = Opened(A, B);
        holder.ViewClosed(B);

        holder.RegisterOpen(B);

        Assert.False(holder.IsKnownClosed(B));
    }

    [Fact]
    public void TheEditorToolsAndTheChat_ReadTheActiveViewThroughOneReader()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var adapter = Path.Combine(dir!.FullName, "Inferpal");
        var frozen = Directory.EnumerateFiles(adapter, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && Path.GetFileName(f) != "VsContextHolder.cs")
            .Where(f => Regex.IsMatch(ConventionCoverageTests.CodeOnly(f), @"GetActiveTextViewAsync\(\s*_contextHolder\.Context"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(frozen.Count == 0, "Resolves the active view from the frozen context: " + string.Join(", ", frozen));

        var surface = ConventionCoverageTests.CodeOnly(Path.Combine(adapter, "Services", "VsIntegration", "VsEditorSurface.cs"));
        Assert.Contains("_contextHolder.ResolveActiveViewAsync(", surface, StringComparison.Ordinal);
    }

    /// <summary>An editor with files open and none active.</summary>
    private sealed class NoFocusEditor(params string[] open) : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => open;
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void FilesOpenAndNoneActive_IsNotNoFileOpen()
    {
        Assert.Equal(Localization.Strings.ActiveDocNoFocus, EditorWriteGate.NoActiveDocument(new NoFocusEditor(A)));
        // Reference arm: nothing open is still said as such.
        Assert.Equal(Localization.Strings.ActiveDocNoFile, EditorWriteGate.NoActiveDocument(new NoFocusEditor()));
    }
}
