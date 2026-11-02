using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An install that finished its first run before the window was sized for the card keeps the factory 8 192 tokens, where
/// most questions about a real project overflow: at an editor's start, a window never sized is sized as the first run
/// does, and said once. And a chat model changed by <c>/model</c> or a picker takes an automatic window with it.
/// </summary>
public sealed class WindowCatchUpTests : IDisposable
{
    private const long Gb = 1L << 30;
    private static readonly ModelArchInfo Llama = new(32, 32, 8, 4096, 131072);

    private readonly string  _path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"catchup-{Guid.NewGuid():N}.json");

    // A file of its own, read and written through the instance seam: the suite's settings path stays where it is, and
    // whatever another test (or a host it started) saves there never lands in this file.
    public WindowCatchUpTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static FakeInferenceProvider Server() => new()
    {
        Installed =
        [
            new InstalledModelInfo("granite4.2:8b", 5 * Gb, ["completion", "tools"]),
            new InstalledModelInfo("big:12b", 12 * Gb, ["completion", "tools"]),
        ],
        OnShow = m => m is "granite4.2:8b" or "big:12b" ? Llama : null,
    };

    /// <summary>The file 1.8.3 wrote for a user who changed nothing — no <c>contextWindowSizeAuto</c> key, the factory
    /// window —, on a card whose memory is known, with the index and completions off (nothing beside the chat).</summary>
    private InferpalConfig LoadAnOldInstall(double cardGb = 24, string provider = "ollama", Action<JsonObject>? change = null)
    {
        var fixture = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "config-1.8.3-factory.json");
        var file = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
        Assert.False(file.ContainsKey("contextWindowSizeAuto"));                                              // witness
        file["provider"]                = provider;
        file["baseUrl"]                 = "http://192.0.2.7:11434";
        file["defaultModel"]            = "granite4.2:8b";
        file["vramBudgetGb"]            = cardGb;
        file["ragEnabled"]              = false;
        file["inlineCompletionEnabled"] = false;
        change?.Invoke(file);
        File.WriteAllText(_path, file.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return InferpalConfig.LoadFileForTests(_path);
    }

    private static Task<string?> CatchUp(InferpalConfig config, FakeInferenceProvider? server = null) =>
        ContextWindowChoice.CatchUpAsync(config, server ?? Server(), config.DefaultModel, CancellationToken.None);

    private static Task<string?> Follow(InferpalConfig config, string model) =>
        ContextWindowChoice.FollowAsync(config, Server(), model, CancellationToken.None);

    [Fact]
    public async Task AnOldInstall_GetsTheWindowItsCardHolds_SaidOnce()
    {
        var config = LoadAnOldInstall();
        Assert.Equal(ContextWindowChoice.Factory, config.ContextWindowSize);                                    // witness

        var notice = await CatchUp(config);

        var written = InferpalConfig.LoadFileForTests(_path);
        Assert.Equal(ContextWindowChoice.MeasuredCeiling, written.ContextWindowSize);
        Assert.Equal(written.ContextWindowSize, written.ContextWindowSizeAuto);
        Assert.Equal(Strings.WindowCaughtUp(ContextWindowChoice.MeasuredCeiling, ContextWindowChoice.Factory, "24",
                                            Strings.SettingsPageServer, Strings.LabelContextWindowSize), notice);
        // The gesture that changes it is named by the labels the settings show.
        Assert.Contains(Strings.LabelContextWindowSize, notice, StringComparison.Ordinal);

        Assert.Null(await CatchUp(config));
        Assert.Null(await CatchUp(InferpalConfig.LoadFileForTests(_path)));
    }

    [Fact]
    public async Task AWindowThePersonChose_AnUnknownCard_ATooSmallGain_OrAnotherServer_AreLeftUnsaid()
    {
        var before = "";
        async Task Unchanged(InferpalConfig config)
        {
            before = File.ReadAllText(_path);
            Assert.Null(await CatchUp(config));
            Assert.Equal(before, File.ReadAllText(_path));
        }

        await Unchanged(LoadAnOldInstall(change: f => f["contextWindowSize"] = 12288));
        await Unchanged(LoadAnOldInstall(cardGb: 0));
        await Unchanged(LoadAnOldInstall(cardGb: 6.5));   // 12 288 tokens fit: below the smallest window worth writing
        await Unchanged(LoadAnOldInstall(provider: "lmstudio"));
        // Reference arm: the same file on a card that holds more is written.
        Assert.NotNull(await CatchUp(LoadAnOldInstall()));
    }

    /// <summary>The person who set 8 192 back by hand after the product sized it: the mark tells that factory window from
    /// one never sized — and nothing resizes it again.</summary>
    [Fact]
    public async Task TheFactoryWindowSetBackByHand_AfterTheProductSizedIt_IsThePersons()
    {
        var config = LoadAnOldInstall();
        Assert.NotNull(await CatchUp(config));
        config.ContextWindowSize = ContextWindowChoice.Factory;   // the settings' field, saved
        config.Save();

        Assert.False(ContextWindowChoice.IsAutomatic(config));
        Assert.Null(await CatchUp(config));
        Assert.Null(await Follow(config, "big:12b"));
        Assert.Equal(ContextWindowChoice.Factory, InferpalConfig.LoadFileForTests(_path).ContextWindowSize);
    }

    private void WriteAsTheOtherEditor(int window, int mark)
    {
        var other = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        other["contextWindowSize"] = window;
        other["contextWindowSizeAuto"] = mark;
        File.WriteAllText(_path, other.ToJsonString());
    }

    /// <summary>The other editor sizes the window WHILE this one asks the server: read again before the write.</summary>
    [Fact]
    public async Task AWindowTheOtherEditorSizedMeanwhile_IsNotWrittenOver_NorSaidAgain()
    {
        var config = LoadAnOldInstall();
        var server = Server();
        var asked = server.OnShow;
        server.OnShow = m => { WriteAsTheOtherEditor(20480, 20480); return asked(m); };

        Assert.Null(await CatchUp(config, server));
        Assert.Equal(20480, InferpalConfig.LoadFileForTests(_path).ContextWindowSize);
    }

    /// <summary><c>Save</c> writes what the copy changed since it read the file: a size decided on the file that equals
    /// what this copy read — the other editor wrote another since — must still reach the file.</summary>
    [Fact]
    public async Task ASizeDecidedOnTheFile_IsWritten_EvenWhenItIsWhatThisCopyRead()
    {
        var mine = LoadAnOldInstall(cardGb: 14);   // read: the factory window, no mark
        WriteAsTheOtherEditor(32768, 32768);        // the other editor caught up since

        // big:12b leaves the card no room: back to the factory window — what this copy read, not what the file holds.
        Assert.NotNull(await Follow(mine, "big:12b"));

        var file = InferpalConfig.LoadFileForTests(_path);
        Assert.Equal((ContextWindowChoice.Factory, 0), (file.ContextWindowSize, file.ContextWindowSizeAuto));
    }

    /// <summary>A start that decided nothing is not asked again for the same server, model and card: the server is not
    /// listed a second time.</summary>
    [Fact]
    public async Task AStartThatDecidedNothing_DoesNotAskTheServerAgain()
    {
        var config = LoadAnOldInstall(cardGb: 6.5, change: f => f["defaultModel"] = "granite4.2:8b-settled");
        var server = Server();
        server.Installed.Add(new InstalledModelInfo("granite4.2:8b-settled", 5 * Gb, ["completion", "tools"]));
        var shown = 0;
        server.OnShow = m => { shown++; return Llama; };

        Assert.Null(await CatchUp(config, server));
        Assert.Null(await CatchUp(config, server));

        Assert.Equal(1, shown);
    }

    /// <summary>A start while the server is down knew nothing (no list, no model shown): not settled, so the start after
    /// the server answers — the reconnection, the next session — sizes the window.</summary>
    [Fact]
    public async Task AStartWhileTheServerIsDown_IsAskedAgain_OnceItAnswers()
    {
        var config = LoadAnOldInstall(change: f => f["defaultModel"] = "granite4.2:8b-down");
        Assert.Null(await CatchUp(config, new FakeInferenceProvider()));   // lists nothing, shows nothing

        var up = Server();
        up.Installed.Add(new InstalledModelInfo("granite4.2:8b-down", 5 * Gb, ["completion", "tools"]));
        up.OnShow = _ => Llama;
        Assert.NotNull(await CatchUp(config, up));
    }

    [Fact]
    public async Task AnAutomaticWindow_FollowsTheModel_DownToTheFactoryOne_AndSaysIt()
    {
        var config = LoadAnOldInstall(cardGb: 14);
        Assert.NotNull(await CatchUp(config));
        Assert.True(InferpalConfig.LoadFileForTests(_path).ContextWindowSize > ContextWindowChoice.Factory);            // witness

        // big:12b leaves a 14 GB card no room for a window sized for it: the factory one, said — never "sized for the card".
        var notice = await Follow(config, "big:12b");

        var written = InferpalConfig.LoadFileForTests(_path);
        Assert.Equal((ContextWindowChoice.Factory, 0), (written.ContextWindowSize, written.ContextWindowSizeAuto));
        Assert.Equal(Strings.WindowBackToFactory(ContextWindowChoice.Factory, "big:12b"), notice);

        // Back to the small model: the window grows again with it.
        Assert.NotNull(await Follow(config, "granite4.2:8b"));
        Assert.True(InferpalConfig.LoadFileForTests(_path).ContextWindowSize > ContextWindowChoice.Factory);
        // The same model again: nothing changes, nothing said.
        Assert.Null(await Follow(config, "granite4.2:8b"));
    }

    [Fact]
    public async Task AWindowThePersonChose_DoesNotFollowTheModel()
    {
        var config = LoadAnOldInstall(change: f => f["contextWindowSize"] = 24576);

        Assert.Null(await Follow(config, "big:12b"));
        Assert.Equal(24576, InferpalConfig.LoadFileForTests(_path).ContextWindowSize);
    }

    // ── The callers: Visual Studio's view model and VS Code's extension do not run in the suite, held on their source ──

    private static string Cs(params string[] path) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), .. path]));

    /// <summary>The text of <paramref name="code"/> from <paramref name="start"/> to the next member at the same indent.</summary>
    private static string Member(string code, string start, string next = "\n    private ")
    {
        var at = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{start}' is not where it was");                                                // WITNESS
        var end = code.IndexOf(next, at + start.Length, StringComparison.Ordinal);
        return end < 0 ? code[at..] : code[at..end];
    }

    [Fact]
    public void VisualStudio_CatchesUpAtTheBackendsAnswer_AfterTheModel_AndTheWindowFollowsEveryModelSwitch()
    {
        var ensure = Member(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"), "private async Task EnsureChatModelAsync");
        var adopt  = ensure.IndexOf("ModelCatalog.FirstModelToAdopt(_config, listed)", StringComparison.Ordinal);
        var catchUp = ensure.IndexOf("ContextWindowChoice.CatchUpAsync(_config, _client, _config.DefaultModel", StringComparison.Ordinal);
        Assert.True(adopt >= 0 && catchUp > adopt, "the window is sized before the model that answers is settled");
        Assert.Contains("FirstRunPresentAsync(windowNotice)", ensure[catchUp..]);

        var pick = Member(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"), "private async Task PickModelAsync");
        Assert.Contains("ContextWindowChoice.FollowAsync(_config, _client, model,", pick);
        // A turn running reads the window at every request: a model picked in the header during it is followed once the
        // turn ends, never under it (the host holds the turn slot, VS Code holds the pick back).
        var waits = pick.IndexOf("await AfterTheTurnAsync();", StringComparison.Ordinal);
        Assert.True(waits >= 0 && waits < pick.IndexOf("ContextWindowChoice.FollowAsync", StringComparison.Ordinal),
                    "the header's pick resizes the window under a running turn");
        Assert.Contains("turn = IsLoading ? _turnDone?.Task : null",
                        Member(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"), "private async Task AfterTheTurnAsync"));
        var model = Member(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"), "case SlashCommandId.Model:", "case SlashCommandId.Tools:");
        Assert.Contains("ContextWindowChoice.FollowAsync(_config, _client, parts[1], ct)", model);
    }

    [Fact]
    public void TheHost_FollowsTheModelOnSlashModel_AndAcpCatchesUpAtTheSessionsStart()
    {
        var model = Member(Cs("Inferpal.Host", "HostSlashCommands.cs"), "case SlashCommandId.Model:", "case SlashCommandId.Tools:");
        Assert.Contains("ContextWindowChoice.FollowAsync(s.Config, s.Client, parts[1], cts.Token)", model);

        var acp = Member(Cs("Inferpal.Host", "Acp", "AcpSession.cs"), "private async Task AdoptDefaultModelAsync");
        var adopt   = acp.IndexOf("\"models/adoptDefault\"", StringComparison.Ordinal);
        var catchUp = acp.IndexOf("\"context/catchUp\"", StringComparison.Ordinal);
        Assert.True(adopt >= 0 && catchUp > adopt, "ACP sizes the window before the model is settled, or not at all");
        Assert.Contains("_startNotice", acp[catchUp..]);
    }

    [Fact]
    public void VsCode_CatchesUpAtStartAndOnReconnection_BeforeTheRingReadsTheWindow_AndThePickerFollows()
    {
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts")));
        string Ts(string name) => Member(code, $"private async {name}(", "\n  private ");

        var ready   = Ts("onHostReady");
        var adopt   = ready.IndexOf("this.adoptDefaultModel(host)", StringComparison.Ordinal);
        var catchUp = ready.IndexOf("this.catchUpWindow(host,", StringComparison.Ordinal);
        var ring    = ready.IndexOf("this.contextWindow = await host.contextWindow()", StringComparison.Ordinal);
        Assert.True(adopt >= 0 && catchUp > adopt && ring > catchUp, "start: model, then window, then the ring");

        var reconnect = Ts("refreshModelsAfterReconnect");
        Assert.True(reconnect.IndexOf("this.catchUpWindow(host,", StringComparison.Ordinal)
                    > reconnect.IndexOf("this.adoptDefaultModel(host)", StringComparison.Ordinal));
        Assert.Contains("host.contextCatchUp()", Ts("catchUpWindow"));
        Assert.Contains("host.contextFollowModel(model)", Ts("pushModelToHost"));
        var written = Ts("windowWritten");
        Assert.Contains("host.contextWindow()", written);
        Assert.Contains("SettingsPanel.onSavedElsewhere()", written);
    }

    [Fact]
    public void VisualStudio_SizesNoWindowForAModelItCouldNotSave()
    {
        var pick = Member(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"), "private async Task PickModelAsync");
        var failed = pick.IndexOf("Strings.SettingsSaveFailed", StringComparison.Ordinal);
        var follow = pick.IndexOf("ContextWindowChoice.FollowAsync", StringComparison.Ordinal);
        Assert.True(failed >= 0 && follow > failed, "PickModelAsync is not where it was");                     // WITNESS
        Assert.Contains("return;", pick[failed..follow]);
    }
}
