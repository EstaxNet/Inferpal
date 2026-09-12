using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Switching workspaces leaves nothing of the previous one in the RAG index — neither in memory nor
/// in the new one's store.
/// </summary>
/// <remarks>
/// The SQLite store is one per root, but <see cref="ProjectIndexService"/> is a single service that
/// keeps its chunks in memory. Two leaks when switching solutions under Visual Studio:
/// <list type="bullet">
///   <item>memory was replaced at the start of the pass only when the new workspace's store already
///         held something — for a project never indexed, <c>search_codebase</c> and the auto-context
///         answered with the PREVIOUS project's code for the whole pass (minutes with embeddings),
///         and forever when the new one has no source file;</item>
///   <item>changes still queued in the previous workspace were drained by the new workspace's pass
///         and re-indexed under its root: A's code written into B's store.</item>
/// </list>
/// </remarks>
public sealed class RagWorkspaceSwitchTests : IDisposable
{
    // Same hook as ProjectIndexServiceWatcherTests: never the user's real index.
    private static readonly string DbBase = InitDbBase();

    private static string InitDbBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragdb-{Guid.NewGuid():N}");
        RagDatabase.BaseDir = () => dir;
        return dir;
    }

    private readonly string _base;
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly List<ProjectIndexService> _services = [];

    public RagWorkspaceSwitchTests()
    {
        _ = DbBase;
        _base  = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragswitch-{Guid.NewGuid():N}");
        _rootA = Directory.CreateDirectory(Path.Combine(_base, "a")).FullName;
        _rootB = Directory.CreateDirectory(Path.Combine(_base, "b")).FullName;
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A C# class large enough to pass the chunkers' MinChunkLines threshold.</summary>
    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        "    public int One()   => 1;",
        "    public int Two()   => 2;",
        "    public int Three() => 3;",
        "    public int Four()  => 4;",
        "    public int Five()  => 5;",
        "    public int Six()   => 6;",
        "}");

    private ProjectIndexService NewService(FakeInferenceProvider provider, int debounceMs = 100)
    {
        var config = new InferpalConfig { RagEnabled = true };
        var svc = new ProjectIndexService(provider, config, new LspSemanticProvider()) { DebounceMs = debounceMs };
        _services.Add(svc);
        return svc;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, Func<string> state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for: {what} (state: {state()})");
    }

    private static async Task<bool> Mentions(ProjectIndexService svc, string word) =>
        (await svc.SearchAsync(null, word, 10, CancellationToken.None))
            .Any(r => r.Chunk.Content.Contains(word, StringComparison.Ordinal));

    [Fact]
    public async Task ANewWorkspace_DoesNotAnswerWithThePreviousWorkspacesCode()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootA, "Alpha.cs"), SampleClass("AlphaProbe"));
        await File.WriteAllTextAsync(Path.Combine(_rootB, "Beta.cs"),  SampleClass("BetaProbe"));

        using var gate    = new ManualResetEventSlim(false);
        using var inPassB = new ManualResetEventSlim(false);
        var provider = new FakeInferenceProvider
        {
            // B's pass stays suspended in its first embedding: the window where the user is already
            // working on B.
            OnEmbedding = text =>
            {
                if (text.Contains("BetaProbe", StringComparison.Ordinal))
                {
                    inPassB.Set();
                    gate.Wait(TimeSpan.FromSeconds(30));
                }
                return [0.1f, 0.2f, 0.3f];
            },
        };

        var svc = NewService(provider);
        svc.StartIndexing(_rootA);
        await WaitUntilAsync(async () => await Mentions(svc, "AlphaProbe") && !svc.IsIndexing,
            "end of the pass over A", () => svc.Status);

        svc.StartIndexing(_rootB);
        try
        {
            Assert.True(inPassB.Wait(TimeSpan.FromSeconds(30)), "the pass over B never asked for an embedding");

            Assert.False(await Mentions(svc, "AlphaProbe"),
                "During the new workspace's pass, search still returns the previous workspace's code.");
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task ChangesQueuedInThePreviousWorkspace_AreNotIndexedIntoTheNewOne()
    {
        var alpha = Path.Combine(_rootA, "Alpha.cs");
        await File.WriteAllTextAsync(alpha, SampleClass("AlphaProbe"));
        await File.WriteAllTextAsync(Path.Combine(_rootB, "Beta.cs"), SampleClass("BetaProbe"));

        // Long debounce: the save in A is still queued when the user moves to B.
        var svc = NewService(new FakeInferenceProvider(), debounceMs: 600_000);
        svc.StartIndexing(_rootA);
        await WaitUntilAsync(async () => await Mentions(svc, "AlphaProbe") && !svc.IsIndexing,
            "end of the pass over A", () => svc.Status);

        svc.OnFileChangedCore(alpha);

        svc.StartIndexing(_rootB);
        await WaitUntilAsync(async () => await Mentions(svc, "BetaProbe") && !svc.IsIndexing,
            "end of the pass over B, drain included", () => svc.Status);

        // Witness: the new workspace is indexed.
        Assert.True(await Mentions(svc, "BetaProbe"));

        Assert.False(await Mentions(svc, "AlphaProbe"),
            "A change queued in the previous workspace was indexed into the new one.");
        var onDiskB = await new RagDatabase(_rootB).LoadAsync(CancellationToken.None);
        Assert.DoesNotContain(onDiskB, c => c.Content.Contains("AlphaProbe", StringComparison.Ordinal));
    }
}
