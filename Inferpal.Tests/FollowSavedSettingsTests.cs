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
            Assert.Equal(Localization.Strings.AcpSettingsUnreadable, broken.Notice);
            Assert.Empty(broken.Taken);
            Assert.False(broken.ServerChanged);
            Assert.Equal("http://my-server:1234", config.BaseUrl);
            Assert.Equal("deny run_command rm", config.PermissionRules);
            Assert.False(config.RagEnabled);

            // Fixed, with one change made meanwhile: that change is taken, and nothing else.
            File.WriteAllText(_path, good.Replace("\"model-a\"", "\"model-b\"", StringComparison.Ordinal));
            var fixedFile = await host.Link.InvokeWithCancellationAsync<Host.ConfigReloadResult>("config/reload");
            Assert.False(fixedFile.Unreadable);
            Assert.Null(fixedFile.Notice);
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
    /// ⚠ The settings window's host follows a new kind of server saved IN the window: its session starts anew, as VS Code
    /// restarts its host after such a save. Kept, its client spoke the old protocol to the new address — the page's model
    /// lists, loaded-models card and Unload all answered for nobody. A new address of the same kind keeps the session.
    /// </summary>
    [Fact]
    public async Task TheSettingsWindow_FollowsANewKindOfServerSavedInIt()
    {
        new InferpalConfig { Provider = "ollama", BaseUrl = "http://127.0.0.1:9", RagEnabled = false, McpEnabled = false }.Save();
        var root = Directory.CreateTempSubdirectory("inferpal-window-kind").FullName;
        var web  = Directory.CreateTempSubdirectory("inferpal-window-kind-web").FullName;
        foreach (var name in new[] { "settings.css", "settings-standalone.css", "settings-standalone.js" })
            File.WriteAllText(Path.Combine(web, name), "/* */");
        using var http = new System.Net.Http.HttpClient();
        try
        {
            // The shipped factories: real clients, on the file above.
            await using var window = await Host.SettingsWindow.SettingsWindowServer.StartAsync(
                root, web, () => new Host.HostServer(), CancellationToken.None);
            Assert.Equal("ollama", Services.Inference.InferenceProviderFactory.CodeOf(window.Host.CurrentSession!.Client));   // WITNESS

            async Task SaveAsync(string provider, string url)
            {
                using var get = new System.Net.Http.StringContent("{\"method\":\"config/get\"}");
                using var got = await http.PostAsync(window.Url + "rpc", get);
                var json = JsonDocument.Parse(await got.Content.ReadAsStringAsync()).RootElement.GetProperty("result").GetString()!;
                var edited = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                edited["provider"] = provider;
                edited["baseUrl"]  = url;
                using var body = new System.Net.Http.StringContent(JsonSerializer.Serialize(new
                {
                    method = "config/update", @params = new { json = edited.ToJsonString(), @base = json },
                }));
                using var saved = await http.PostAsync(window.Url + "rpc", body);
                Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            }

            await SaveAsync("lmstudio", "http://127.0.0.1:10");
            var session = window.Host.CurrentSession!;
            Assert.Equal("lmstudio", Services.Inference.InferenceProviderFactory.CodeOf(session.Client));
            Assert.Equal("http://127.0.0.1:10", session.Client.ServerAddress);

            // Reference arm: a new address of the same kind applies live, in the same session.
            await SaveAsync("lmstudio", "http://127.0.0.1:11");
            Assert.Same(session, window.Host.CurrentSession);
            Assert.Equal("http://127.0.0.1:11", session.Client.ServerAddress);
        }
        finally
        {
            foreach (var dir in new[] { root, web })
                try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ }
        }
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

    // ── An editor's copy follows the file before each question (SettingsFollow) ─────────────

    private static Services.Inference.IInferenceProvider OllamaClient() =>
        Services.Inference.InferenceProviderFactory.Create(new InferpalConfig { Provider = "ollama" });

    [Fact]
    public void AnEditorsCopy_TakesWhatTheOtherEditorSaved_AndKeepsWhatItChangedItself()
    {
        new InferpalConfig { PermissionRules = "", CustomSystemPrompt = "" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        vs.CustomSystemPrompt = "answer in French";   // this window's own change, not saved yet

        WriteAsTheSettingsWindow(c => c.PermissionRules = "deny run_command ^git push");
        var follow = Services.Inference.SettingsFollow.Take(vs, OllamaClient());

        Assert.Equal(["permissionRules"], follow.Taken);
        Assert.Equal("deny run_command ^git push", vs.PermissionRules);
        Assert.Equal("answer in French", vs.CustomSystemPrompt);

        // Taken once: the next question takes nothing more.
        Assert.Empty(Services.Inference.SettingsFollow.Take(vs, OllamaClient()).Taken);
    }

    /// <summary>Reference arm: what this copy saved itself is not taken back as another editor's change.</summary>
    [Fact]
    public void WhatThisCopySaved_IsNotTakenAsAnotherEditorsChange()
    {
        new InferpalConfig { CommandTimeoutSeconds = 120 }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        vs.CommandTimeoutSeconds = 200;
        vs.Save();

        Assert.Empty(Services.Inference.SettingsFollow.Take(vs, OllamaClient()).Taken);
        Assert.Equal(200, vs.CommandTimeoutSeconds);
    }

    [Fact]
    public void ANewKindOfServer_IsSaidOnce_AndTheCopyKeepsTheServerItSpeaks()
    {
        new InferpalConfig { Provider = "ollama", BaseUrl = "http://localhost:11434" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();

        vs.DefaultModel = "qwen3:8b";
        vs.Save();
        WriteAsTheSettingsWindow(c => { c.Provider = "lmstudio"; c.BaseUrl = "http://localhost:1234"; c.DefaultModel = "lm-model"; });
        var follow = Services.Inference.SettingsFollow.Take(vs, OllamaClient());

        Assert.True(follow.ServerChanged);
        Assert.Equal("lmstudio", follow.SwitchTo);
        Assert.Equal(("ollama", "http://localhost:11434"), (vs.Provider, vs.BaseUrl));   // its client speaks Ollama
        // The new server's model is not asked of the old one: left aside with it.
        Assert.Equal("qwen3:8b", vs.DefaultModel);
        Assert.False(Services.Inference.SettingsFollow.Take(vs, OllamaClient()).ServerChanged);   // said once
    }

    [Fact]
    public void AnUnreadableFile_TakesNothing_AndIsNotTheFactoryValues()
    {
        new InferpalConfig { PermissionRules = "deny run_command ^rm" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        File.WriteAllText(_path, "{ not json");

        var follow = Services.Inference.SettingsFollow.Take(vs, OllamaClient());

        Assert.True(follow.Unreadable);
        Assert.Empty(follow.Taken);
        Assert.Equal("deny run_command ^rm", vs.PermissionRules);
    }

    [Fact]
    public void AFollow_IsAppliedAsASave()
    {
        var config = new InferpalConfig();
        var seen = new List<string>();
        config.Followed += (taken, kind) => seen.AddRange([.. taken, kind ?? "-"]);
        config.Saved += () => seen.Add("saved");

        config.NotifyFollowed(["language", "mcpServersJson"]);
        config.NotifyFollowed([], "lmstudio");   // a new kind only: said, nothing saved

        Assert.Equal(["language", "mcpServersJson", "-", "saved", "lmstudio"], seen);
    }

    /// <summary>A save of this copy merges what the other editor saved meanwhile into the file: the next follow still
    /// takes it — the followed file moves by what this save wrote, not by the whole merged file.</summary>
    [Fact]
    public void ASaveOfThisCopy_DoesNotSwallowTheOtherEditorsChange()
    {
        new InferpalConfig { PermissionRules = "", DefaultModel = "a" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        WriteAsTheSettingsWindow(c => c.PermissionRules = "deny run_command ^git push");

        vs.DefaultModel = "b";   // /model in Visual Studio, before its next question
        vs.Save();
        var follow = Services.Inference.SettingsFollow.Take(vs, OllamaClient());

        Assert.Equal(["permissionRules"], follow.Taken);
        Assert.Equal("deny run_command ^git push", vs.PermissionRules);
    }

    /// <summary>Saved here, then put back by the other editor: the old value is the other editor's change, and taken.</summary>
    [Fact]
    public void ASettingSavedHere_ThenPutBackElsewhere_IsFollowed()
    {
        new InferpalConfig { CommandTimeoutSeconds = 120 }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        vs.CommandTimeoutSeconds = 200;
        vs.Save();
        WriteAsTheSettingsWindow(c => c.CommandTimeoutSeconds = 120);

        Assert.Equal(["commandTimeoutSeconds"], Services.Inference.SettingsFollow.Take(vs, OllamaClient()).Taken);
        Assert.Equal(120, vs.CommandTimeoutSeconds);
    }

    /// <summary>A list merged item by item: the other editor's item, written by this copy's save, is still followed.</summary>
    [Fact]
    public void AListItemTheOtherEditorAdded_IsFollowed_AfterThisCopysSaveMergedIt()
    {
        new InferpalConfig { PermissionRules = "" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        WriteAsTheSettingsWindow(c => c.PermissionRules = "deny run_command ^git push");
        vs.AddLines(nameof(InferpalConfig.PermissionRules), ["allow run_command ^dotnet test"]);   // an "Always" here
        Assert.Contains("deny run_command ^git push", OnDisk().PermissionRules);   // witness: the file holds both

        Services.Inference.SettingsFollow.Take(vs, OllamaClient());
        Assert.Contains("deny run_command ^git push", vs.PermissionRules);
    }

    /// <summary>The same through a save of this copy, which merges the list item by item.</summary>
    [Fact]
    public void AListItemTheOtherEditorAdded_IsFollowed_AfterThisCopysSaveMergedItInTheFile()
    {
        new InferpalConfig { PermissionRules = "" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        WriteAsTheSettingsWindow(c => c.PermissionRules = "deny run_command ^git push");
        vs.PermissionRules = "allow run_command ^dotnet test";
        vs.Save();
        Assert.Contains("deny run_command ^git push", OnDisk().PermissionRules);   // witness: merged in the file

        Services.Inference.SettingsFollow.Take(vs, OllamaClient());
        Assert.Contains("deny run_command ^git push", vs.PermissionRules);
    }

    /// <summary>A rule this copy added, removed by the other editor before the next question: the removal is followed.</summary>
    [Fact]
    public void ALineAddedHere_RemovedElsewhere_IsFollowed()
    {
        new InferpalConfig { PermissionRules = "" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        vs.AddLines(nameof(InferpalConfig.PermissionRules), ["allow run_command ^dotnet test"]);
        WriteAsTheSettingsWindow(c => c.PermissionRules = "");

        Services.Inference.SettingsFollow.Take(vs, OllamaClient());
        Assert.Equal("", vs.PermissionRules);
    }

    /// <summary>The settings window and the chat share one copy: a new kind of server seen first by the window is still
    /// said — the handler hears it from whichever gesture followed.</summary>
    [Fact]
    public void ANewKindOfServer_FollowedByAnyGesture_IsHandedToTheHandlers()
    {
        new InferpalConfig { Provider = "ollama" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        string? said = null;
        vs.Followed += (_, kind) => said = kind;
        WriteAsTheSettingsWindow(c => c.Provider = "lmstudio");

        Services.Inference.SettingsFollow.TakeAndApply(vs, OllamaClient());
        Assert.Equal("lmstudio", said);
    }

    /// <summary>A gesture during a run (the away card's Retry, a slash command) takes nothing: the agent loop reads its rules
    /// and its models. The chat window says so as the settings window does.</summary>
    [Fact]
    public void TheChatWindow_TakesNothingUnderARunningTurn() =>
        Assert.Contains("SettingsFollow.TakeAndApply(_config, _client, () => GpuScheduler.IsTurnInFlight)", ConventionCoverageTests.CodeOnly(
            Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs")), StringComparison.Ordinal);

    /// <summary>Never while a turn runs: the agent loop reads these settings.</summary>
    [Fact]
    public void ATurnInFlight_TakesNothing_TheNextQuestionDoes()
    {
        new InferpalConfig { PermissionRules = "" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        WriteAsTheSettingsWindow(c => c.PermissionRules = "deny run_command ^rm");

        Assert.Empty(Services.Inference.SettingsFollow.TakeAndApply(vs, OllamaClient(), busy: () => true).Taken);
        Assert.Equal(["permissionRules"], Services.Inference.SettingsFollow.TakeAndApply(vs, OllamaClient(), busy: () => false).Taken);
    }

    /// <summary>The server a follow left aside stays the file's: a save that must merge over this copy's reading (the file
    /// unreadable at that moment) does not write the old kind back.</summary>
    [Fact]
    public void AServerLeftAside_IsNotWrittenBack_WhenASaveMergesOverItsReading()
    {
        new InferpalConfig { Provider = "ollama", BaseUrl = "http://localhost:11434" }.Save();
        var vs = InferpalConfig.LoadOwnCopy();
        WriteAsTheSettingsWindow(c => { c.Provider = "lmstudio"; c.BaseUrl = "http://localhost:1234"; });
        Services.Inference.SettingsFollow.Take(vs, OllamaClient());

        File.WriteAllText(_path, "{ not json");   // held by another writer at the moment of the save
        vs.CommandTimeoutSeconds = 77;
        vs.Save();

        Assert.Equal(("lmstudio", "http://localhost:1234", 77), (OnDisk().Provider, OnDisk().BaseUrl, OnDisk().CommandTimeoutSeconds));
    }

    /// <summary>Visual Studio follows before each question and as its settings window opens; VS Code before each question
    /// and as its panel loads — read on the source: neither front-end can be driven from the suite.</summary>
    [Fact]
    public void BothEditors_FollowTheFile_BeforeEachQuestion_AndAsTheirSettingsOpen()
    {
        var repo = ConventionCoverageTests.RepoRoot();
        var tool = Path.Combine(repo, "Inferpal", "ToolWindow");
        Assert.Matches(@"PinWorkspaceRoot\(\);\s*await FollowSettingsFileAsync\(\);",
                       ConventionCoverageTests.CodeOnly(Path.Combine(tool, "InferpalToolWindowData.ChatTurn.cs")));
        Assert.Matches(@"try\s*\{\s*await FollowSettingsFileAsync\(\);\s*switch \(SlashCommandRouter\.Route",
                       ConventionCoverageTests.CodeOnly(Path.Combine(tool, "InferpalToolWindowData.SlashCommands.cs")));
        Assert.Contains("_config.Followed                += OnSettingsFollowed;",
                        ConventionCoverageTests.CodeOnly(Path.Combine(tool, "InferpalToolWindowData.Construction.cs")), StringComparison.Ordinal);
        Assert.Matches(@"SettingsFollow\.TakeAndApply\(config, client, \(\) => GpuScheduler\.IsTurnInFlight\)[\s\S]{0,900}_opened\s*= config\.SnapshotNow\(\);",
                       ConventionCoverageTests.CodeOnly(Path.Combine(tool, "InferpalSettingsData.cs")));

        Assert.Matches(@"await this\.followSettings\(host\);\s*const agentMode = this\.windowAgentMode\(\);",
                       WebviewRebuildTests.TsCode("chatViewProvider.ts"));
        // The host reports a change once: the panel's follow goes to the chat too.
        Assert.Matches(@"await this\.shell\.followed\(await host\.configReload\(\)\)[\s\S]{0,300}this\.lastConfigJson = await host\.configGet\(\);",
                       WebviewRebuildTests.TsCode("settingsBridge.ts"));
        Assert.Contains("SettingsPanel.adoptFollowed = (followed) => chatView.applyFollowed(followed);",
                        WebviewRebuildTests.TsCode("extension.ts"), StringComparison.Ordinal);
        Assert.Contains("configReload: () => call('config/reload')", WebviewRebuildTests.TsCode("webview/settingsStandaloneShell.ts"),
                        StringComparison.Ordinal);
        Assert.Contains("config/reload", Inferpal.Host.SettingsWindow.SettingsWindowServer.FormMethods);
        // The host's followed file moves by what a save wrote, never by the whole merged file.
        foreach (var file in new[] { "HostServer.cs", "HostSetup.cs" })
            Assert.DoesNotContain("s.FileSettings = (_fileSettings() ?? s.Config).SnapshotNow();",
                                  ConventionCoverageTests.CodeOnly(Path.Combine(repo, "Inferpal.Host", file)), StringComparison.Ordinal);
    }
}
