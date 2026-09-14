using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A pass that finds nothing changed leaves the stored index alone. Every start of the editor runs a
/// full verification pass, and its end deleted and re-inserted every row and vector of the project —
/// the whole index in disk writes, for the same content.
/// </summary>
public sealed class RagUnchangedPassTests : IDisposable
{
    private readonly string _root;

    public RagUnchangedPassTests()
    {
        TestRagStore.Redirect();
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragunchanged-{Guid.NewGuid():N}")).FullName;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
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

    /// <summary>One start of the editor: a fresh service runs its full pass to the end.</summary>
    private async Task RunPassAsync(FakeInferenceProvider provider)
    {
        using var svc = new ProjectIndexService(provider, new InferpalConfig { RagEnabled = true },
                                                new LspSemanticProvider());
        svc.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(svc.Status.StartsWith("RAG: ✅", StringComparison.Ordinal) && !svc.IsIndexing))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass did not finish (status: {svc.Status})");
            await Task.Delay(50);
        }
    }

    private static List<long> RowIds(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM chunks ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    [Fact]
    public async Task APassThatFindsNothingChanged_DoesNotRewriteTheStoredIndex()
    {
        var file = Path.Combine(_root, "Alpha.cs");
        await File.WriteAllTextAsync(file, SampleClass("AlphaProbe"));
        var provider = new FakeInferenceProvider { OnEmbedding = _ => [0.1f, 0.2f, 0.3f] };

        await RunPassAsync(provider);
        var dbPath = new RagDatabase(_root).DbPath;
        var stored = RowIds(dbPath);
        Assert.NotEmpty(stored);   // witness: the first pass did store the index

        await RunPassAsync(provider);   // the next start: same files, same embedding model
        Assert.Equal(stored, RowIds(dbPath));

        // Witness: a pass that does find a change still persists it.
        await File.WriteAllTextAsync(file, SampleClass("BetaProbe"));
        await RunPassAsync(provider);
        var chunks = await new RagDatabase(_root).LoadAsync(CancellationToken.None);
        Assert.Contains(chunks, c => c.Content.Contains("BetaProbe", StringComparison.Ordinal));
        Assert.DoesNotContain(chunks, c => c.Content.Contains("AlphaProbe", StringComparison.Ordinal));
    }
}
