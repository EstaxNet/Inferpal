using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A file saved while the chat is busy is re-indexed only once it is idle — after a debounce, and
/// the embedding waits for the GPU the agent holds for its whole turn. So a file the agent writes is
/// NOT in the semantic index for the rest of that turn, and <c>search_codebase</c> answered about the
/// version from before, or "nothing found" about a class it had just created, with nothing saying so.
/// </summary>
public sealed class SearchWhileNotReindexedTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"stale-{Guid.NewGuid():N}")).FullName;
    private readonly List<ProjectIndexService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Class(string name) =>
        $"public class {name}\n{{\n    public int One() => 1;\n    public int Two() => 2;\n}}\n";

    private async Task<(ProjectIndexService Index, SemanticSearchTool Tool)> IndexedAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        var provider = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        var config   = new InferpalConfig { RagEnabled = true };
        var index    = new ProjectIndexService(provider, config, new LspSemanticProvider());
        _services.Add(index);

        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!index.Status.Contains('✅') && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Contains("✅", index.Status);                      // witness: the first pass ran

        return (index, new SemanticSearchTool(index, provider, config));
    }

    private static Task<string> Search(SemanticSearchTool tool, string query) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query }), CancellationToken.None);

    [Fact]
    public async Task AFileNotYetReindexed_IsNamed_AboveTheResults()
    {
        var (index, tool) = await IndexedAsync();
        index.DebounceMs  = 60_000;                              // the change stays pending for the test

        var beta = Path.Combine(_root, "Beta.cs");
        await File.WriteAllTextAsync(beta, Class("Beta"));
        index.OnFileChangedCore(beta);

        var answer = await Search(tool, "Beta");

        Assert.Contains("not re-indexed yet", answer);
        Assert.Contains("Beta.cs", answer[..answer.IndexOf('\n')]);   // named on the first line, above the report
        Assert.Contains("search_in_files", answer);
    }

    [Fact]
    public async Task AnUpToDateIndex_SaysNothingMore()
    {
        // Reference arm: nothing pending, nothing said.
        var (_, tool) = await IndexedAsync();

        Assert.DoesNotContain("not re-indexed yet", await Search(tool, "Alpha"));
    }

    [Fact]
    public async Task OnceReindexed_TheFileIsNoLongerNamed()
    {
        var (index, tool) = await IndexedAsync();
        index.DebounceMs  = 50;

        var beta = Path.Combine(_root, "Beta.cs");
        await File.WriteAllTextAsync(beta, Class("Beta"));
        index.OnFileChangedCore(beta);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (index.NotYetReindexed.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.Empty(index.NotYetReindexed);
        Assert.DoesNotContain("not re-indexed yet", await Search(tool, "Beta"));
    }
}
