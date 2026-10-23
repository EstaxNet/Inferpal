using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A completion asked for by its key (Alt+') says why it brings nothing — the sentence the status bar shows in both
/// editors, decided once (<see cref="AskedCompletion.Refusal"/>).
/// </summary>
/// <remarks>A pause that brings nothing stays quiet; a key that brings nothing looks broken. Each cause named here was a
/// silent return of the completion path: switched off, an excluded file, no model to complete with on an
/// OpenAI-compatible server, the chat answering on the same card.</remarks>
[Collection(SignalCollection.Name)]
public sealed class AskedCompletionTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();   // the chat-busy marker is a signal: never the real folder
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-asked").FullName;

    public void Dispose()
    {
        _signals.Dispose();
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ACompletionThatCanGo_IsNotRefused()
    {
        // Reference arm: every other test changes one thing from this.
        Assert.Null(AskedCompletion.Refusal(new InferpalConfig(), Path.Combine(_root, "App.cs")));
    }

    [Fact]
    public void SwitchedOff_SaysSo_AndWhere()
    {
        var refusal = AskedCompletion.Refusal(new InferpalConfig { InlineCompletionEnabled = false }, null);

        Assert.Equal(Strings.CompletionAskedOff(Strings.SettingsPageAutocomplete), refusal);
    }

    [Fact]
    public void AnExcludedFile_IsRefused_WithTheRuleThatExcludesIt()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "secret/\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
        var excluded = Path.Combine(_root, "secret", "Vault.cs");

        var refusal = AskedCompletion.Refusal(new InferpalConfig(), excluded);

        Assert.NotNull(refusal);
        Assert.Contains(".aiignore", refusal);
        Assert.Null(AskedCompletion.Refusal(new InferpalConfig(), Path.Combine(_root, "App.cs")));   // witness
    }

    [Fact]
    public void AnOpenAiCompatibleServerWithoutACompletionModel_IsNamed()
    {
        var config = new InferpalConfig { Provider = "openai-compatible", DefaultModel = "big-chat", InlineCompletionModel = "" };

        Assert.Equal(Strings.CompletionAskedNoModel(Strings.SettingsPageAutocomplete), AskedCompletion.Refusal(config, null));
        config.InlineCompletionModel = "coder";
        Assert.Null(AskedCompletion.Refusal(config, null));
    }

    [Fact]
    public void TheChatAnsweringOnTheSameMachine_KeepsTheCard_AndSaysSo()
    {
        using var lease = GpuScheduler.AcquireChatLease();

        Assert.Equal(Strings.CompletionAskedChatBusy, AskedCompletion.Refusal(new InferpalConfig(), null));
    }

    [Fact]
    public void TheChatAnsweringOnAnotherMachine_DoesNotHoldBackACompletionHere()
    {
        var config = new InferpalConfig
        {
            BaseUrl = "http://192.0.2.10:11434",
            InlineCompletionRunsOn = "other", InlineCompletionBaseUrl = "http://127.0.0.1:11434",
            InlineCompletionProvider = "ollama", InlineCompletionModel = "coder",
        };
        using var lease = GpuScheduler.AcquireChatLease();

        Assert.Null(AskedCompletion.Refusal(config, null));
    }

    [Theory]
    [InlineData("onDemand", true)]
    [InlineData("OnDemand", true)]
    [InlineData(" ondemand ", true)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("manual", false)]
    public void OnlyTheOnDemandValue_TurnsThePauseOff(string? trigger, bool onDemand) =>
        Assert.Equal(onDemand, AskedCompletion.IsOnDemand(trigger));

    [Fact]
    public void AnEmptyAnswer_NamesTheServer_WhenTheServerFailed()
    {
        Assert.Equal(Strings.CompletionAskedServerFailed, AskedCompletion.Empty(serverFailed: true));
        Assert.Equal(Strings.CompletionAskedNothing, AskedCompletion.Empty(serverFailed: false));
    }

    [Fact]
    public void TheFactoryValue_IsAuto() => Assert.Equal(AskedCompletion.Auto, new InferpalConfig().InlineCompletionTrigger);

    [Fact]
    public void AHandWrittenForm_IsReadAsTheCodeOfItsOption()
    {
        var config = new InferpalConfig { InlineCompletionTrigger = "OnDemand" };

        InferpalConfig.CanonicalizeChoices(config);

        Assert.Equal(AskedCompletion.OnDemand, config.InlineCompletionTrigger);
    }
}
