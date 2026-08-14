using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Marks the tests that repoint <see cref="InferpalConfig.OverridePathForTests"/>, which is
/// <b>static</b>: while they run, every other test's <c>Config.Save()</c> — <c>/model</c> and the
/// settings RPC do call it — lands on their file and drops the cache they are asserting on. xUnit
/// parallelises across collections, so the isolation has to be declared, not assumed.
/// </summary>
[CollectionDefinition(GlobalConfigPathCollection.Name, DisableParallelization = true)]
public sealed class GlobalConfigPathCollection
{
    public const string Name = "global-config-path";
}

/// <summary>
/// <see cref="InferpalConfig.Load"/> sits on hot paths — the ghost-text controller calls it on
/// every keystroke — so it caches the parse and invalidates on the file's write stamp. These tests
/// pin both halves: the cache must actually be reused, and it must never serve stale values after
/// a save.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public class ConfigLoadCacheTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"cache-{Guid.NewGuid():N}.json");

    public ConfigLoadCacheTests() => InferpalConfig.OverridePathForTests = _path;

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void RepeatedLoads_OfAnUnchangedFile_ReturnTheSameInstance()
    {
        new InferpalConfig { DefaultModel = "m1" }.Save();

        var first  = InferpalConfig.Load();
        var second = InferpalConfig.Load();

        Assert.Same(first, second);
    }

    [Fact]
    public void SaveInvalidatesTheCache_EvenWithinTheSameFileTimeTick()
    {
        // The stamp has limited resolution; a save must drop the cache itself rather than hope
        // the timestamp moved.
        new InferpalConfig { DefaultModel = "before" }.Save();
        Assert.Equal("before", InferpalConfig.Load().DefaultModel);

        new InferpalConfig { DefaultModel = "after" }.Save();

        Assert.Equal("after", InferpalConfig.Load().DefaultModel);
    }

    [Fact]
    public void AnExternalRewrite_IsPickedUp()
    {
        // The other editor (or a hand edit) writes the shared file; Load must notice.
        new InferpalConfig { DefaultModel = "vs" }.Save();
        Assert.Equal("vs", InferpalConfig.Load().DefaultModel);

        var external = JsonSerializer.Serialize(new InferpalConfig { DefaultModel = "vscode" });
        File.WriteAllText(_path, external);
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(1));

        Assert.Equal("vscode", InferpalConfig.Load().DefaultModel);
    }

    [Fact]
    public void MissingFile_YieldsDefaultsWithoutThrowing()
    {
        try { File.Delete(_path); } catch { }

        Assert.NotNull(InferpalConfig.Load());
    }

    [Fact]
    public void CorruptFile_YieldsDefaultsWithoutThrowing()
    {
        File.WriteAllText(_path, "{ not json");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(2));

        Assert.NotNull(InferpalConfig.Load());
    }
}
