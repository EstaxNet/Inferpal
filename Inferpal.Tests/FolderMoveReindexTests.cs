using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A folder renamed, moved or deleted is followed by both indexes.
//
//  The watcher listened to source files only: a folder event names no file and was never seen. After
//  renaming src/Billing to src/Payments, search_codebase kept answering with files that no longer
//  existed and found nothing under the new name, and rename_symbol — whose spans come from the C#
//  index — left every reference inside the moved folder untouched under "found N occurrences".
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection("Diagnostics")]
public sealed class FolderMoveReindexTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public FolderMoveReindexTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src", "Old"));
        File.WriteAllText(Path.Combine(_root, "src", "Old", "Widget.cs"), string.Join('\n',
            "public class Widget",
            "{",
            "    public int One()   => 1;",
            "    public int Two()   => 2;",
            "    public int Three() => 3;",
            "    public int Four()  => 4;",
            "    public int Five()  => 5;",
            "}"));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string OldFile => Path.Combine(_root, "src", "Old", "Widget.cs");
    private string NewFile => Path.Combine(_root, "src", "New", "Widget.cs");

    private ProjectIndexService IndexedService()
    {
        var provider = new FakeInferenceProvider { OnEmbedding = _ => [0.1f, 0.2f, 0.3f] };
        var svc = new ProjectIndexService(provider, new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed" },
                                          new LspSemanticProvider()) { DebounceMs = 100 };
        _services.Add(svc);
        svc.StartIndexing(_root);
        return svc;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"timed out waiting for: {what}");
    }

    private async Task<int> ChunksOf(ProjectIndexService svc, string file) =>
        (await svc.GetFileChunksAsync(file, _root, CancellationToken.None)).Count;

    [Fact]
    public async Task AFolderRenamed_IsIndexedUnderItsNewName_AndNotUnderTheOld()
    {
        var svc = IndexedService();
        await WaitUntilAsync(async () => await ChunksOf(svc, OldFile) > 0, "the initial pass indexed the file");

        Directory.Move(Path.Combine(_root, "src", "Old"), Path.Combine(_root, "src", "New"));

        await WaitUntilAsync(async () => await ChunksOf(svc, NewFile) > 0 && await ChunksOf(svc, OldFile) == 0,
                             "the index follows the renamed folder");
    }

    [Fact]
    public async Task AFolderMovedOutOfTheWorkspace_LeavesTheIndex()
    {
        // The Recycle Bin's shape: the folder leaves the tree in one move, and no file inside raises an event.
        // (A recursive delete removes the files one by one, each with its own event — that case always worked.)
        var svc = IndexedService();
        await WaitUntilAsync(async () => await ChunksOf(svc, OldFile) > 0, "the initial pass indexed the file");

        var outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"bin-{Guid.NewGuid():N}");
        Directory.Move(Path.Combine(_root, "src", "Old"), outside);
        try
        {
            await WaitUntilAsync(async () => await ChunksOf(svc, OldFile) == 0, "the moved-out folder's file left the index");
        }
        finally { try { Directory.Delete(outside, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void TheCSharpIndex_FollowsAMovedFolder()
    {
        CSharpSemanticIndex.ResetCacheForTests();
        var index = CSharpSemanticIndex.ForWorkspace(_root);
        index.Build();
        Assert.Contains("Old", index.FindReferences("Widget").Declaration!.RelPath);   // witness: indexed where it was

        Directory.Move(Path.Combine(_root, "src", "Old"), Path.Combine(_root, "src", "New"));
        CSharpSemanticIndex.NotifyDirectoryChanged(Path.Combine(_root, "src", "Old"));
        CSharpSemanticIndex.NotifyDirectoryChanged(Path.Combine(_root, "src", "New"));

        var declaration = index.FindReferences("Widget").Declaration;
        Assert.NotNull(declaration);
        Assert.Contains("New", declaration!.RelPath);
        Assert.Equal(1, index.FileCount);
    }

    [Fact]
    public async Task ChangesAWatcherLost_AreReadFromTheDisk_ByBothIndexes()
    {
        var svc = IndexedService();
        await WaitUntilAsync(async () => await ChunksOf(svc, OldFile) > 0, "the initial pass indexed the file");
        CSharpSemanticIndex.ResetCacheForTests();
        var index = CSharpSemanticIndex.ForWorkspace(_root);
        index.Build();

        // A buffer that overflowed: the file below is written and no event names it.
        foreach (var name in new[] { "_watcher", "_dirWatcher", "_rootWatcher" })
            if (typeof(ProjectIndexService).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(svc) is FileSystemWatcher watcher)
                watcher.EnableRaisingEvents = false;
        var added = Path.Combine(_root, "src", "Old", "Gadget.cs");
        File.WriteAllText(added, string.Join('\n', "public class Gadget", "{", "    public int One() => 1;", "    public int Two() => 2;", "}"));

        svc.OnEventsLost(new InternalBufferOverflowException());

        await WaitUntilAsync(async () => await ChunksOf(svc, added) > 0, "the search index read the file no event named");
        await WaitUntilAsync(() => Task.FromResult(index.FindReferences("Gadget").Declaration is not null), "the C# index read it too");
    }

    [Fact]
    public async Task AWatcherThatFailed_IsArmedAgain_AndOneThatOverflowedIsKept()
    {
        var svc = IndexedService();
        await WaitUntilAsync(async () => await ChunksOf(svc, OldFile) > 0, "the initial pass indexed the file");
        var field = typeof(ProjectIndexService).GetField("_watcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Reference arm: a watcher whose buffer overflowed still raises events.
        var watcher = field.GetValue(svc);
        svc.OnEventsLost(new InternalBufferOverflowException());
        svc.SetRoot(_root);
        Assert.Same(watcher, field.GetValue(svc));

        // A watcher that failed (a network share gone for a moment) raises nothing more: the next SetRoot replaces it.
        svc.OnEventsLost(new IOException("the share went away"));
        svc.SetRoot(_root);
        Assert.NotNull(field.GetValue(svc));
        Assert.NotSame(watcher, field.GetValue(svc));
    }
}
