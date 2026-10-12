using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A copy that FOLLOWS the file (an ACP thread, whose settings the browser settings window saves from elsewhere) takes
/// what the file changed — as the file's: never written back by its own later save, never over what it changed itself.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class FollowSavedSettingsTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path     =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"follow-{Guid.NewGuid():N}.json");

    public FollowSavedSettingsTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private void WriteAsTheSettingsWindow(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
    }

    private InferpalConfig OnDisk() => JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;

    [Fact]
    public void ASettingFollowedFromTheFile_IsNotWrittenBackByTheThreadsLaterSave()
    {
        new InferpalConfig { CommandTimeoutSeconds = 120, DefaultModel = "a" }.Save();
        var thread = InferpalConfig.LoadOwnCopy();
        var before = thread.SnapshotNow();

        WriteAsTheSettingsWindow(c => c.CommandTimeoutSeconds = 130);
        Assert.Equal(["commandTimeoutSeconds"], thread.TakeFileChanges(before, InferpalConfig.LoadOwnCopy()));
        Assert.Equal(130, thread.CommandTimeoutSeconds);

        // Saved again elsewhere, then the thread saves a setting of its own: the file's newer value stands.
        WriteAsTheSettingsWindow(c => c.CommandTimeoutSeconds = 150);
        thread.DefaultModel = "b";
        thread.Save();
        Assert.Equal(150, OnDisk().CommandTimeoutSeconds);
        Assert.Equal("b", OnDisk().DefaultModel);
    }

    [Fact]
    public void WhatTheThreadChangedItself_StaysWhenTheFileDidNotChangeIt()
    {
        new InferpalConfig { DefaultModel = "a" }.Save();
        var thread = InferpalConfig.LoadOwnCopy();
        var before = thread.SnapshotNow();
        thread.UseDefaultModelForSession("the-threads-pick");

        WriteAsTheSettingsWindow(c => c.CommandTimeoutSeconds = 99);
        Assert.Equal(["commandTimeoutSeconds"], thread.TakeFileChanges(before, InferpalConfig.LoadOwnCopy()));
        Assert.Equal("the-threads-pick", thread.DefaultModel);

        // Reference arm: nothing changed since — nothing taken.
        Assert.Empty(thread.TakeFileChanges(InferpalConfig.LoadOwnCopy().SnapshotNow(), InferpalConfig.LoadOwnCopy()));
    }

    /// <summary>
    /// Two hosts in one process — an ACP agent's two threads — built as the shipped agent builds them: a model set for
    /// one thread is not the other's.
    /// </summary>
    [Fact]
    public async Task TwoHostsInOneProcess_EachHoldTheirOwnSettings()
    {
        new InferpalConfig { DefaultModel = "model-a", RagEnabled = false }.Save();
        var root = Directory.CreateTempSubdirectory("inferpal-two-hosts").FullName;
        try
        {
            await using var first  = await HostAsync(root);
            await using var second = await HostAsync(root);
            await first.Link.InvokeWithParameterObjectAsync("models/useForSession", new { model = "model-b" });

            Assert.Equal("model-b", first.Server.CurrentSession!.Config.DefaultModel);
            Assert.Equal("model-a", second.Server.CurrentSession!.Config.DefaultModel);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ } }
    }

    private sealed record LiveHost(Host.HostServer Server, StreamJsonRpc.JsonRpc HostSide, StreamJsonRpc.JsonRpc Link) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Link.Dispose();
            HostSide.Dispose();
            Server.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<LiveHost> HostAsync(string root)
    {
        var provider = new FakeInferenceProvider { ModelNames = ["model-a", "model-b"] };
        var (hostStream, linkStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        var server   = new Host.HostServer(_ => provider);   // no configuration injected: the shipped default
        var hostSide = Host.HostRpc.Create(hostStream, hostStream, server);
        server.Attach(hostSide);
        hostSide.StartListening();
        var link = Host.HostRpc.Create(linkStream, linkStream);
        link.StartListening();
        await link.InvokeWithParameterObjectAsync<JsonElement>("initialize", new { rootDir = root, clientName = "test", editorSurface = false });
        return new LiveHost(server, hostSide, link);
    }

    [Fact]
    public void EachCopy_IsItsOwn_WhileLoad_SharesOne()
    {
        new InferpalConfig { DefaultModel = "a" }.Save();
        Assert.NotSame(InferpalConfig.LoadOwnCopy(), InferpalConfig.LoadOwnCopy());
        // Reference arm: the cache this copy exists to step around.
        Assert.Same(InferpalConfig.Load(), InferpalConfig.Load());
    }
}
