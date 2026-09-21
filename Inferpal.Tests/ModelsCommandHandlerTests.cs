using System.IO;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

public class ModelsCommandHandlerTests
{
    private static string[] Cmd(params string[] args) => ["/models", .. args];

    /// <summary>A config with no backend URL: the empty-list branch then cannot ask anyone, and
    /// keeps answering what it always did. The tests that DO exercise the check set one.</summary>
    private static readonly Inferpal.Config.InferpalConfig Cfg = new() { BaseUrl = "" };

    private static RunningModelInfo Run(string name, long vramBytes) => new(name, vramBytes, "");

    [Fact]
    public async Task Delete_BackendWithoutModelManagement_ReturnsUnsupported()
    {
        var client = new FakeInferenceProvider { Capabilities = ProviderCapabilities.OpenAiCompatible };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("delete", "foo"), CancellationToken.None);

        Assert.Equal(Strings.ModelsBackendUnsupported, result.Message);
        Assert.Empty(client.Deleted); // never reached the backend
    }

    [Fact]
    public async Task Running_BackendWithoutVramMonitoring_ReturnsUnsupported()
    {
        var client = new FakeInferenceProvider { Capabilities = ProviderCapabilities.OpenAiCompatible };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("running"), CancellationToken.None);

        Assert.Equal(Strings.ModelsBackendUnsupported, result.Message);
    }

    [Fact]
    public async Task Delete_NoArg_ReturnsUsage()
    {
        var client = new FakeInferenceProvider();

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("delete"), CancellationToken.None);

        Assert.Equal(Strings.ModelsDeleteUsage, result.Message);
        Assert.Empty(client.Deleted);
    }

    [Fact]
    public async Task Delete_Success_CallsBackendAndConfirms()
    {
        var client = new FakeInferenceProvider { OnDelete = _ => true };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("delete", "llama3.1"), CancellationToken.None);

        Assert.Equal(Strings.ModelsDeleted("llama3.1"), result.Message);
        Assert.Equal("llama3.1", Assert.Single(client.Deleted));
    }

    [Fact]
    public async Task Delete_Failure_ReportsFailure()
    {
        var client = new FakeInferenceProvider { OnDelete = _ => false };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("delete", "ghost"), CancellationToken.None);

        Assert.Equal(Strings.ModelsDeleteFailed("ghost"), result.Message);
    }

    [Fact]
    public async Task Running_None_ReturnsEmptyNotice()
    {
        var client = new FakeInferenceProvider { Running = [] };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("running"), CancellationToken.None);

        Assert.Equal(Strings.ModelsNoneRunning, result.Message);
    }

    [Fact]
    public async Task Running_WithModels_ListsThem()
    {
        var client = new FakeInferenceProvider { Running = [Run("qwen3", 5L * 1024 * 1024 * 1024)] };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("running"), CancellationToken.None);

        Assert.Contains("qwen3", result.Message);
    }

    [Fact]
    public async Task List_None_ReturnsHint()
    {
        var client = new FakeInferenceProvider { ModelNames = [] };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd(), CancellationToken.None);

        Assert.Equal(Strings.ModelsNoneInstalled, result.Message);
    }

    [Fact]
    public async Task List_WithModels_RendersTableWithLoadedMarker()
    {
        var client = new FakeInferenceProvider
        {
            ModelNames = ["llama3.1", "qwen3"],
            Running    = [Run("qwen3", 4L * 1024 * 1024 * 1024)],
        };

        var result = await ModelsCommandHandler.HandleAsync(client, Cfg, Cmd("list"), CancellationToken.None);

        Assert.Contains("llama3.1", result.Message);
        Assert.Contains("qwen3", result.Message);
        Assert.Contains("🟢", result.Message); // qwen3 is loaded
    }

    // ── /model <name> ──────────────────────────────────────────────────────────

    /// <summary>
    /// A name the backend does not serve is named at the moment it is set: "Model → X" alone was a
    /// success announcement, and the failure only came with the next message.
    /// </summary>
    [Theory]
    [InlineData("qwen2.5-coder:14b")]   // another tag of an installed model
    [InlineData("qwen3")]               // Ollama reads it as qwen3:latest, which is not installed
    [InlineData("qwen2.5-codr:7b")]     // typo
    public async Task Switch_ToAModelTheBackendDoesNotList_Warns(string model)
    {
        var client = new FakeInferenceProvider { ModelNames = ["qwen2.5-coder:7b", "qwen3:8b"] };

        var message = await ModelsCommandHandler.SwitchMessageAsync(client, model, CancellationToken.None);

        Assert.StartsWith(Strings.SlashModelChanged(model), message);
        Assert.Contains(Strings.SlashModelNotListed(model), message);
    }

    [Theory]
    [InlineData("llama3.1")]            // implicit :latest
    [InlineData("LLAMA3.1:latest")]
    [InlineData("qwen3:8b")]
    public async Task Switch_ToAListedModel_OnlyConfirms(string model)
    {
        var client = new FakeInferenceProvider { ModelNames = ["llama3.1:latest", "qwen3:8b"] };

        var message = await ModelsCommandHandler.SwitchMessageAsync(client, model, CancellationToken.None);

        Assert.Equal(Strings.SlashModelChanged(model), message);
    }

    /// <summary>An empty list cannot tell "not served" from "backend unreachable": no warning.</summary>
    [Fact]
    public async Task Switch_WhenTheBackendListsNothing_OnlyConfirms()
    {
        var client = new FakeInferenceProvider { ModelNames = [] };

        var message = await ModelsCommandHandler.SwitchMessageAsync(client, "anything", CancellationToken.None);

        Assert.Equal(Strings.SlashModelChanged("anything"), message);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void BothFrontEnds_AnswerModelThroughTheSharedMessage()
    {
        var vm   = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        var host = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Host", "HostSlashCommands.cs"));

        // Witness: /model is still handled in these two files.
        Assert.Contains("SlashCommandId.Model:", vm);
        Assert.Contains("SlashCommandId.Model:", host);

        Assert.Contains("ModelsCommandHandler.SwitchMessageAsync(", vm);
        Assert.Contains("ModelsCommandHandler.SwitchMessageAsync(", host);
        Assert.DoesNotContain("Strings.SlashModelChanged(", vm);
        Assert.DoesNotContain("Strings.SlashModelChanged(", host);
    }

    // ── An empty list: nothing installed, or nobody to ask ────────────────────

    private static Inferpal.Config.InferpalConfig Reachable(string url = "http://127.0.0.1:11434") =>
        new() { BaseUrl = url };

    /// <summary>
    /// ⚠ An unreachable backend NEVER throws here: all three providers catch, trace and
    /// <c>return []</c>. So "no model installed" was the answer given when the backend was simply
    /// not running — and on a product whose prerequisite is `ollama serve`, that is the single most
    /// likely state. It sent the user to install models instead of starting the server.
    /// ⚠ The ambiguity was already written down in the handler, for its other reader:
    /// <c>SwitchMessageAsync</c> "cannot judge, so it adds nothing". This one can, by asking.
    /// </summary>
    [Fact]
    public async Task List_WithAnUnreachableBackend_NamesIt_InsteadOfSayingNothingIsInstalled()
    {
        var client = new FakeInferenceProvider { ModelNames = [], ConnectionOk = false };

        var result = await ModelsCommandHandler.HandleAsync(
            client, Reachable(), Cmd(), CancellationToken.None);

        Assert.Equal(Strings.MsgUnreachable("http://127.0.0.1:11434"), result.Message);
        Assert.NotEqual(Strings.ModelsNoneInstalled, result.Message);
    }

    /// <summary>Same conflation on the other listing.</summary>
    [Fact]
    public async Task Running_WithAnUnreachableBackend_NamesIt_InsteadOfSayingNothingRuns()
    {
        var client = new FakeInferenceProvider { Running = [], ConnectionOk = false };

        var result = await ModelsCommandHandler.HandleAsync(
            client, Reachable(), Cmd("running"), CancellationToken.None);

        Assert.Equal(Strings.MsgUnreachable("http://127.0.0.1:11434"), result.Message);
    }

    /// <summary>Reference arm: a backend that ANSWERS and really has nothing installed keeps the
    /// ordinary sentence — otherwise the fix trades one wrong cause for another.</summary>
    [Fact]
    public async Task List_WithAReachableBackendAndNoModel_StillSaysNothingIsInstalled()
    {
        var client = new FakeInferenceProvider { ModelNames = [], ConnectionOk = true };

        var result = await ModelsCommandHandler.HandleAsync(
            client, Reachable(), Cmd(), CancellationToken.None);

        Assert.Equal(Strings.ModelsNoneInstalled, result.Message);
    }

    /// <summary>Reference arm: a non-empty list never pays the extra round-trip, and says what it
    /// always said.</summary>
    [Fact]
    public async Task List_WithModels_IsUnchanged_AndAsksNobody()
    {
        var client = new FakeInferenceProvider { ModelNames = ["llama3.1"], ConnectionOk = false };

        var result = await ModelsCommandHandler.HandleAsync(
            client, Reachable(), Cmd(), CancellationToken.None);

        Assert.Contains("llama3.1", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot reach", result.Message, StringComparison.Ordinal);
    }
}
