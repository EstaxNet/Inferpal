using System.IO;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The default chat model is the code's, and a fresh install's backend usually does not have it. VS Code has no first
/// run, and the Visual Studio one was spent when the backend was down at that moment: every question then went to a
/// model that is not there, and failed on "model not found, try pulling it first".
/// </summary>
public class UnchosenDefaultModelTests
{
    private static readonly string CodeDefault = new InferpalConfig().DefaultModel;

    [Fact]
    public void AnUnchosenDefaultTheBackendLacks_IsReplacedByTheBestInstalledChatModel()
    {
        var config = new InferpalConfig();

        var adopted = ModelCatalog.FirstModelToAdopt(config, ["nomic-embed-text", "devstral-small-2", "qwen3:8b"]);

        Assert.Equal(ModelCatalog.PickBestChatModel(["devstral-small-2", "qwen3:8b"]), adopted);
    }

    [Theory]
    [InlineData(true)]   // listed under its tag: the backend HAS it
    [InlineData(false)]
    public void TheDefault_IsKept_WhenTheBackendHasIt(bool tagged)
    {
        var config = new InferpalConfig();
        var listed = tagged ? $"{CodeDefault}:latest" : CodeDefault;

        Assert.Null(ModelCatalog.FirstModelToAdopt(config, ["qwen3:8b", listed]));
    }

    [Fact]
    public void AModelSomeoneChose_IsNeverReplaced_EvenWhenTheBackendLacksIt()
    {
        // A choice that is missing is a fact the error names ("not found"); replacing it would ask another model.
        var config = new InferpalConfig { DefaultModel = "my-finetune" };

        Assert.Null(ModelCatalog.FirstModelToAdopt(config, ["qwen3:8b"]));
    }

    [Theory]
    [InlineData]                                   // nothing listed: unreachable, or nothing installed
    [InlineData("nomic-embed-text")]               // embedding models only
    public void NothingIsAdopted_WithoutAnInstalledChatModel(params string[] listed) =>
        Assert.Null(ModelCatalog.FirstModelToAdopt(new InferpalConfig(), listed));

    // ── Visual Studio: not executable from the suite (the window's view model), held on its source ──

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheFirstRun_EndsOnlyOnceAModelIsChosen()
    {
        var code  = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        var start = code.IndexOf("private async Task RunSetupDiscoveryAsync", StringComparison.Ordinal);
        var pick  = code.IndexOf("ModelCatalog.PickBestChatModel", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && pick > start, "the setup discovery or its model choice is not where it was"); // WITNESS

        // Before the model is chosen — backend down, nothing installed — the first run stays to be completed.
        Assert.DoesNotContain("IsFirstRun = false", code[start..pick]);
        Assert.Contains("IsFirstRun = false", code[pick..]);
    }

    [Fact]
    public void APull_ChoosesTheModel_AndTheAutomaticFirstRunDoesNotRepeatTheHeartbeat()
    {
        var root = RepoRoot();
        var slash = ConventionCoverageTests.CodeOnly(
            Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        var pull = slash[slash.IndexOf("private async Task PullModelInteractiveAsync", StringComparison.Ordinal)..];
        Assert.Contains("_client.PullModelAsync(", pull);                                                   // WITNESS
        // The model just downloaded is used now, not at the next start.
        Assert.Contains("if (ok) await EnsureChatModelAsync();", pull[..pull.IndexOf("\n    }", StringComparison.Ordinal)]);

        var rag = ConventionCoverageTests.CodeOnly(
            Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        Assert.Contains("RunSetupDiscoveryAsync(FirstRunPresentAsync, CancellationToken.None, automatic: true)", rag);
        // The heartbeat's first failed check already says the backend is down: the first run, which resumes at every
        // start until a model is chosen, said it a second time at every start.
        var unreachable = rag[rag.IndexOf("if (!reachable)", StringComparison.Ordinal)..];
        Assert.True(unreachable.IndexOf("if (automatic) return;", StringComparison.Ordinal)
                    is var at and >= 0 && at < unreachable.IndexOf("await present(", StringComparison.Ordinal));
    }

    // ── VS Code: the extension has no test runner, held on its source ──

    /// <summary>The body of the TypeScript method <paramref name="name"/>, comments neutralized.</summary>
    private static string TsMethod(string code, string name)
    {
        var start = code.IndexOf($"private async {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} is not where it was");                                              // WITNESS
        var next = code.IndexOf("\n  private ", start + 1, StringComparison.Ordinal);
        return next < 0 ? code[start..] : code[start..next];
    }

    [Fact]
    public void VsCode_TheBackendComingBack_RelistsTheModels_AndAdopts()
    {
        // Started after the editor — the ordinary order — the backend left the picker on "no model listed" and every
        // question on the default until a reload: the list read while it was down was empty, and nothing re-read it.
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "src", "chatViewProvider.ts")));

        var poll = TsMethod(code, "pollBackendStatus");
        Assert.Contains("this.refreshModelsAfterReconnect(host)", poll);

        var refresh = TsMethod(code, "refreshModelsAfterReconnect");
        Assert.Contains("host.modelsList()", refresh);
        Assert.Contains("this.adoptDefaultModel(host)", refresh);

        Assert.Contains("host.modelsAdoptDefault()", TsMethod(code, "adoptDefaultModel"));
    }

    [Fact]
    public void TheBackendComingBack_CompletesTheFirstRun_OrAdoptsAnInstalledModel()
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"));

        Assert.Contains("ConnectionTransition.Restored", code);                                              // WITNESS
        Assert.Contains("_ = EnsureChatModelAsync();", code);
        Assert.Contains("StartFirstRunDiscoveryAsync()", code);
        Assert.Contains("ModelCatalog.FirstModelToAdopt(_config, listed)", code);
        Assert.Contains("Strings.MsgModelAdopted(configured, adopted)", code);
    }
}
