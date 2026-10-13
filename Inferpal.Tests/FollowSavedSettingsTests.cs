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

    /// <summary>
    /// ⚠ A settings file a hand edit broke is not "the factory values": an open thread keeps every setting it had — its
    /// model server, its permission rules, code search off — and follows the file again once it can be read. Read as
    /// factory values, one comma too many sent every open thread to localhost:11434, emptied its deny rules and started
    /// indexing the folder.
    /// </summary>
    [Fact]
    public async Task AnUnreadableFile_IsFollowedByNothing_AndFollowedAgainOnceFixed()
    {
        new InferpalConfig
        {
            DefaultModel = "model-a", RagEnabled = false, BaseUrl = "http://my-server:1234",
            PermissionRules = "deny run_command rm",
        }.Save();
        var good = File.ReadAllText(_path);
        var root = Directory.CreateTempSubdirectory("inferpal-unreadable").FullName;
        try
        {
            await using var host = await HostAsync(root);
            var config = host.Server.CurrentSession!.Config;
            Assert.Equal("http://my-server:1234", config.BaseUrl);   // WITNESS: the host started on the file

            File.WriteAllText(_path, good.TrimEnd()[..^1] + ",}");   // one comma too many
            var broken = await host.Link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload");
            Assert.True(broken.Unreadable);
            Assert.Empty(broken.Taken);
            Assert.False(broken.ServerChanged);
            Assert.Equal("http://my-server:1234", config.BaseUrl);
            Assert.Equal("deny run_command rm", config.PermissionRules);
            Assert.False(config.RagEnabled);

            // Fixed, with one change made meanwhile: that change is taken, and nothing else.
            File.WriteAllText(_path, good.Replace("\"model-a\"", "\"model-b\"", StringComparison.Ordinal));
            var fixedFile = await host.Link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload");
            Assert.False(fixedFile.Unreadable);
            Assert.Equal(["defaultModel"], fixedFile.Taken);
            Assert.Equal("model-b", config.DefaultModel);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ } }
    }

    /// <summary>
    /// ⚠ A thread's client speaks one kind of server's protocol and reads its address and key live. A new KIND of server
    /// saved in the settings waits for a new thread: the thread keeps its server whole and says so once. A new address
    /// of the same kind applies at the next question, with nothing to say. Taken whole, the new kind sent the old
    /// protocol to the new address — every turn refused while the new server ran — and an address change was announced
    /// as "this thread still talks to the one it started with" while it no longer did.
    /// </summary>
    [Fact]
    public async Task ANewKindOfServer_WaitsForANewThread_ANewAddressAppliesAtOnce()
    {
        new InferpalConfig { Provider = "ollama", BaseUrl = "http://127.0.0.1:9", RagEnabled = false }.Save();
        var root = Directory.CreateTempSubdirectory("inferpal-new-server").FullName;
        var (hostStream, linkStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new Host.HostServer();   // the shipped factories: real clients, the file above
        using var hostSide = Host.HostRpc.Create(hostStream, hostStream, server);
        server.Attach(hostSide);
        hostSide.StartListening();
        using var link = Host.HostRpc.Create(linkStream, linkStream);
        link.StartListening();
        try
        {
            await link.InvokeWithParameterObjectAsync<JsonElement>("initialize", new { rootDir = root, clientName = "test", editorSurface = false });
            var session = server.CurrentSession!;
            Assert.Equal("http://127.0.0.1:9", session.Client.ServerAddress);   // WITNESS: a real client, on the file

            WriteAsTheSettingsWindow(c => { c.Provider = "lmstudio"; c.BaseUrl = "http://127.0.0.1:10"; });
            var newKind = await link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload");
            Assert.True(newKind.ServerChanged);
            Assert.DoesNotContain("provider", newKind.Taken);
            Assert.DoesNotContain("baseUrl", newKind.Taken);
            Assert.Equal("ollama", session.Config.Provider);
            Assert.Equal("http://127.0.0.1:9", session.Client.ServerAddress);
            Assert.False((await link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload")).ServerChanged);   // said once

            // The thread's own kind again, at a new address: applies now, nothing to say.
            WriteAsTheSettingsWindow(c => { c.Provider = "ollama"; c.BaseUrl = "http://127.0.0.1:11"; });
            var newAddress = await link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload");
            Assert.False(newAddress.ServerChanged);
            Assert.Contains("baseUrl", newAddress.Taken);
            Assert.Equal("http://127.0.0.1:11", session.Client.ServerAddress);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ } }
    }

    /// <summary>
    /// ⚠ What the thread saves itself (here a pin) moves the file as the thread last took it: the settings window then
    /// taking the pin out is followed at the next question. Left behind, the file's value equalled the old one and the
    /// thread kept its pin for the rest of its life. A setting the window changed meanwhile is still followed.
    /// </summary>
    [Fact]
    public async Task WhatTheThreadSavedItself_AndTheWindowPutBack_IsFollowed()
    {
        new InferpalConfig { DefaultModel = "model-a", RagEnabled = false, CommandTimeoutSeconds = 120 }.Save();
        var root = Directory.CreateTempSubdirectory("inferpal-own-save").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "pinned.md"), "notes\n");
            await using var host = await HostAsync(root);
            var config = host.Server.CurrentSession!.Config;

            WriteAsTheSettingsWindow(c => c.CommandTimeoutSeconds = 300);   // meanwhile, in the window
            await host.Link.InvokeWithParameterObjectAsync<JsonElement>("pins/add", new { path = "pinned.md" });
            Assert.Contains("pinned.md", OnDisk().PinnedContextFiles, StringComparison.Ordinal);   // WITNESS: the thread saved

            WriteAsTheSettingsWindow(c => c.PinnedContextFiles = string.Empty);
            var reloaded = await host.Link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload");
            Assert.Contains("pinnedContextFiles", reloaded.Taken);
            Assert.Equal(string.Empty, config.PinnedContextFiles);
            Assert.Contains("commandTimeoutSeconds", reloaded.Taken);
            Assert.Equal(300, config.CommandTimeoutSeconds);
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
