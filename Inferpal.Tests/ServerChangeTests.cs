using System.IO;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A change of the chat's server takes the graphics memory and the models of the roles on the chat's server with it
/// (<see cref="ServerChange"/>): the old machine's card sized the new one's models and context, and an embedding model the
/// new server does not have turned code search to keywords in silence.
/// </summary>
public class ServerChangeTests
{
    private static InferpalConfig Old() => new()
    {
        Provider = "ollama", BaseUrl = "http://localhost:11434", VramBudgetGb = 24,
        InlineCompletionModel = "qwen2.5-coder:1.5b", RagEmbeddingModel = "embeddinggemma:latest",
    };

    private static InferpalConfig Copy(InferpalConfig c) =>
        System.Text.Json.JsonSerializer.Deserialize<InferpalConfig>(System.Text.Json.JsonSerializer.Serialize(c))!;

    [Fact]
    public void AnotherServer_StartsTheOldServersCardAndModelsOver()
    {
        var before = Old();
        var after = Copy(before);
        after.Provider = "lmstudio";
        after.BaseUrl  = "http://192.168.1.2:1234";

        Assert.True(ServerChange.Follow(before, after));
        Assert.Equal((0.0, "", ""), (after.VramBudgetGb, after.InlineCompletionModel, after.RagEmbeddingModel));
    }

    /// <summary>Reference arm: the same server written another way changes nothing.</summary>
    [Fact]
    public void TheSameServer_WrittenAnotherWay_KeepsEverything()
    {
        var before = Old();
        var after = Copy(before);
        after.BaseUrl = "http://localhost:11434/";

        Assert.False(ServerChange.Follow(before, after));
        Assert.Equal((24.0, "qwen2.5-coder:1.5b", "embeddinggemma:latest"),
                     (after.VramBudgetGb, after.InlineCompletionModel, after.RagEmbeddingModel));
    }

    [Fact]
    public void WhatTheSameGestureChose_IsKept()
    {
        var before = Old();
        var after = Copy(before);
        after.BaseUrl          = "http://192.168.1.2:11434";
        after.VramBudgetGb     = 12;                 // the new server's card, typed in the same save
        after.RagEmbeddingModel = "nomic-embed-text"; // a model of the new server, picked in the same save

        ServerChange.Follow(before, after);
        Assert.Equal((12.0, "", "nomic-embed-text"), (after.VramBudgetGb, after.InlineCompletionModel, after.RagEmbeddingModel));
    }

    [Fact]
    public void ARoleOnAServerOfItsOwn_KeepsItsModel()
    {
        var before = Old();
        // Completion on a server of its own, at the old chat address: it stays on the machine it was set up on.
        before.InlineCompletionRunsOn  = RoleServers.OnOther;
        before.InlineCompletionBaseUrl = "http://localhost:11434";
        var after = Copy(before);
        after.BaseUrl = "http://192.168.1.2:1234";

        ServerChange.Follow(before, after);
        Assert.Equal("qwen2.5-coder:1.5b", after.InlineCompletionModel);
        Assert.Equal("", after.RagEmbeddingModel);
    }

    [Fact]
    public void ARoleOnAServerOfItsOwn_AtTheNewChatAddress_KeepsItsModel()
    {
        var before = Old();
        // Completion already runs on the machine the chat moves to: its server does not move.
        before.InlineCompletionRunsOn  = RoleServers.OnOther;
        before.InlineCompletionBaseUrl = "http://192.168.1.2:11434";
        var after = Copy(before);
        after.BaseUrl = "http://192.168.1.2:11434";

        ServerChange.Follow(before, after);
        Assert.Equal("qwen2.5-coder:1.5b", after.InlineCompletionModel);
    }

    [Fact]
    public void ACardWrittenWithTwoDecimals_IsTheOldCard_AsTheBoxShowsIt()
    {
        var before = Old();
        before.VramBudgetGb = 7.75;
        var after = Copy(before);
        after.BaseUrl      = "http://192.168.1.2:11434";
        after.VramBudgetGb = 7.8;   // what the settings box gives back

        ServerChange.Follow(before, after);
        Assert.Equal(0.0, after.VramBudgetGb);
    }

    /// <summary>Visual Studio's settings window and its <c>/setup</c> adopting a server go through the rule, and the boxes
    /// show what it started over — read on the source: the view models cannot be built in the suite.</summary>
    [Fact]
    public void VisualStudio_FollowsTheRule_WhereTheAddressChanges()
    {
        var folder = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow");
        var settings = ConventionCoverageTests.CodeOnly(Path.Combine(folder, "InferpalSettingsData.cs"));
        // Judged on the live settings once the form's changes are laid over them, and the boxes show what started over.
        Assert.Matches(@"var before = Services\.Inference\.ServerChange\.Before\(_config\);\s*_config\.ApplyChangesFrom\(edited, _opened\);"
                       + @"\s*if \(Services\.Inference\.ServerChange\.Follow\(before, _config\)\)", settings);
        Assert.Contains("RagEmbeddingModel     = edited.RagEmbeddingModel;", settings, StringComparison.Ordinal);
        var setup = ConventionCoverageTests.CodeOnly(Path.Combine(folder, "InferpalToolWindowData.Rag.cs"));
        Assert.Matches(@"_config\.BaseUrl  = url = found\.Url;\s*Services\.Inference\.ServerChange\.Follow\(before, _config\);", setup);
        // The file read first: a model another editor saved for the old server is the old server's too.
        Assert.Contains("var before = Services.Inference.ServerChange.Before(_config);", setup, StringComparison.Ordinal);
    }
}

public partial class HostServerTests
{
    /// <summary>The VS Code settings panel moves the chat to another server: the old server's card and models start over,
    /// and the panel is told to show the saved settings again.</summary>
    [Fact]
    public async Task ConfigUpdate_ToAnotherServer_StartsTheOldServersCardAndModelsOver()
    {
        using var h = CreateHarness(cfg =>
        {
            cfg.VramBudgetGb = 24;
            cfg.InlineCompletionModel = "qwen2.5-coder:1.5b";
            cfg.RagEmbeddingModel = "embeddinggemma:latest";
        });
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var opened = await h.Client.InvokeAsync<string>("config/get");

        var panel = System.Text.Json.Nodes.JsonNode.Parse(opened)!.AsObject();
        panel["baseUrl"] = "http://192.168.1.2:1234";
        var result = await h.Client.InvokeWithParameterObjectAsync<Host.ConfigUpdateResult>("config/update",
            new { json = panel.ToJsonString(), @base = opened });

        Assert.True(result.ServerReset);
        var after = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        Assert.Equal(0.0, after["vramBudgetGb"]!.GetValue<double>());
        Assert.Equal("", after["inlineCompletionModel"]!.GetValue<string>());
        Assert.Equal("", after["ragEmbeddingModel"]!.GetValue<string>());
    }

    /// <summary>A card measured after the panel opened (the first run, <c>/hardware</c>) is the old server's too: judged on
    /// the live settings, not on the copy the panel opened with.</summary>
    [Fact]
    public async Task ConfigUpdate_ToAnotherServer_StartsOverACardSetSinceThePanelOpened()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var opened = await h.Client.InvokeAsync<string>("config/get");   // the panel opens: no card yet

        var meanwhile = System.Text.Json.Nodes.JsonNode.Parse(opened)!.AsObject();
        meanwhile["vramBudgetGb"] = 24;
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = meanwhile.ToJsonString() });

        var panel = System.Text.Json.Nodes.JsonNode.Parse(opened)!.AsObject();
        panel["baseUrl"] = "http://192.168.1.2:1234";
        var result = await h.Client.InvokeWithParameterObjectAsync<Host.ConfigUpdateResult>("config/update",
            new { json = panel.ToJsonString(), @base = opened });

        Assert.True(result.ServerReset);
        var after = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        Assert.Equal(0.0, after["vramBudgetGb"]!.GetValue<double>());
    }

    /// <summary>The same through a <c>config/update</c> that sends the whole object without its opening copy.</summary>
    [Fact]
    public async Task ConfigUpdate_WithoutABase_ToAnotherServer_StartsOverToo()
    {
        using var h = CreateHarness(cfg => { cfg.VramBudgetGb = 24; cfg.RagEmbeddingModel = "embeddinggemma:latest"; });
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var whole = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        whole["baseUrl"] = "http://192.168.1.2:1234";

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.ConfigUpdateResult>("config/update",
            new { json = whole.ToJsonString() });

        Assert.True(result.ServerReset);
        var after = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        Assert.Equal((0.0, ""), (after["vramBudgetGb"]!.GetValue<double>(), after["ragEmbeddingModel"]!.GetValue<string>()));
    }
}
