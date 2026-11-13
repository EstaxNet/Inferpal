using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's chat window, run without Visual Studio: its view model on a fake model server, with the real approval
/// service, tools and turn. What the window does is tested here by running it; a test of its behaviour does not read its text.
/// </summary>
/// <remarks>
/// A user of this class joins <see cref="SignalCollection"/>: the folder Visual Studio has open is a signal file. The
/// <c>VisualStudioExtensibility</c> is <c>null</c>: a gesture that opens a document, a dialog or a tool window reaches the editor
/// itself, and stays out of what this can run. ⚠ Without a folder open, the window looks near the process's own folder, which
/// under a test is this repository: nothing that writes, git above all, runs without one. A regression would commit here.
/// </remarks>
internal sealed class VsWindowHarness : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly List<IDisposable> _owned = [];

    public string Root { get; }
    public FakeInferenceProvider Fake { get; }
    public ToolRegistry Tools { get; }
    public InferpalToolWindowData Window { get; }

    /// <param name="fake">The model server.</param>
    /// <param name="folderOpen">Whether Visual Studio has <see cref="Root"/> open.</param>
    /// <param name="root">A folder the test prepared before the window opens it; deleted with this.</param>
    /// <param name="config">The window's settings, for a test that reads or follows them.</param>
    /// <param name="rootless">Whether the window starts without any workspace, as Visual Studio opened on nothing: it does
    /// not look near the process's folder (this repository, under a test). Implies no folder open.</param>
    /// <param name="client">The client every service holds, in place of <paramref name="fake"/> (Visual Studio's
    /// <see cref="SwitchingProvider"/> over fakes).</param>
    public VsWindowHarness(FakeInferenceProvider? fake = null, bool folderOpen = true, string? root = null, InferpalConfig? config = null,
                           bool rootless = false, IInferenceProvider? client = null)
    {
        Root = root ?? Directory.CreateTempSubdirectory("inferpal-vm").FullName;
        if (rootless) folderOpen = false;
        SignalFile._isProcessAliveOverride = _ => true;
        if (folderOpen) ActiveSolutionSignal.WriteFolder(Root);
        Fake = fake ?? new FakeInferenceProvider();
        // RAG off: on, the window's start would index whatever repository holds the test, once the test's folder is gone.
        config ??= new InferpalConfig { DefaultModel = "m" };
        config.RagEnabled = false;
        config.IsFirstRun = false;   // a first run probes the servers of the machine the suite runs on
        var holder = new VsContextHolder();
        var editor = new NullEditorSurface();
        IInferenceProvider held = client ?? Fake;
        var index  = Owned(new ProjectIndexService(held, config, new LspSemanticProvider()));
        if (folderOpen) index.SetRoot(Root);
        var docs   = Owned(new DocsIndexService(held, config));
        var approval = new VsApprovalService(null!, config, index, holder);
        Tools  = new ToolRegistry(editor, approval, config, index, held, new ProjectMapService(editor),
                                  new McpToolService(config, approval, clientFactory: null, workspaceRoot: () => index.RootDir),
                                  docs, new OpenDocumentOverlay(), new NullDebugSession());
        // Static for the window's start, which reads it after the constructor returns: reset by Dispose, or here when the
        // window never comes to be.
        InferpalToolWindowData.ProcessFolderIgnoredForTests = rootless;
        try
        {
            Window = new InferpalToolWindowData(held, Tools, config, null!, holder, index, Owned(new ModelLifetimeService(held, config)),
                                                Owned(new VsBuildMonitor()), docs);
        }
        catch
        {
            InferpalToolWindowData.ProcessFolderIgnoredForTests = false;
            throw;
        }
    }

    private T Owned<T>(T service) where T : IDisposable
    {
        _owned.Add(service);
        return service;
    }

    /// <summary>Types <paramref name="text"/> and presses Enter; completes when the turn has ended.</summary>
    public Task AskAsync(string text)
    {
        Window.Prompt = text;
        return Window.SendCommand.ExecuteAsync(null, null, CancellationToken.None);
    }

    /// <summary>Waits for what a turn does on another thread, within the budget a test that waits gets.</summary>
    /// <remarks>The window's context writes <c>Messages</c> while this reads it: a read it interrupted is a read to do again.</remarks>
    public static async Task UntilAsync(Func<bool> done)
    {
        bool Now()
        {
            try { return done(); }
            catch (InvalidOperationException) { return false; }
        }
        for (var i = 0; i < 300 && !Now(); i++) await Task.Delay(100);
        Assert.True(Now(), "what the test waits for never came");
    }

    public void Dispose()
    {
        Window.StopHeartbeatForTests();
        InferpalToolWindowData.ProcessFolderIgnoredForTests = false;
        foreach (var service in _owned) service.Dispose();
        SignalFile._isProcessAliveOverride = null;
        SignalScope.ResetForTests();
        _scratch.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
