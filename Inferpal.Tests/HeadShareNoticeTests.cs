using System;
using System.Collections.Generic;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When the system prompt and the tool definitions take more than half the window a turn is measured against, the turn
/// says so, once for a root, model, window and tools — with the remedy that works on the person's server. At 8K the
/// built-in tools alone take three quarters (pari 76's <c>read20</c>): said nowhere but in the X-Ray before.
/// </summary>
public sealed class HeadShareNoticeTests
{
    // read20's head: the built-in tools and a 5 000-character system prompt.
    private const int Tools = 17812 / 4, System = 5027 / 4;

    private static string Root() => Path.Combine(Path.GetTempPath(), "headshare-" + Guid.NewGuid().ToString("N"));
    private static HashSet<string> Said() => [];

    [Fact]
    public void At8K_TheHeadIsSaid_OnceForTheSameWindow_AgainForAnother()
    {
        var root = Root();
        var said = Said();

        var notice = ContextManager.HeadShareNotice(root, "granite", InferenceProviderFactory.Ollama, 8192, 8192, System, Tools, said);

        Assert.NotNull(notice);
        Assert.Contains(Strings.HeadShareRemedyOllama, notice);
        Assert.Contains(Strings.ContextPartTools(Tools), notice);
        Assert.Null(ContextManager.HeadShareNotice(root, "granite", InferenceProviderFactory.Ollama, 8192, 8192, System, Tools, said));
        Assert.NotNull(ContextManager.HeadShareNotice(root, "granite", InferenceProviderFactory.Ollama, 6144, 6144, System, Tools, said));
        // Another session (an ACP thread, another window) says it for itself.
        Assert.NotNull(ContextManager.HeadShareNotice(root, "granite", InferenceProviderFactory.Ollama, 8192, 8192, System, Tools, Said()));
    }

    /// <summary>Reference arm: the same head at 16K is a third of the window — nothing said.</summary>
    [Fact]
    public void At16K_TheSameHead_IsNotSaid() =>
        Assert.Null(ContextManager.HeadShareNotice(Root(), "granite", InferenceProviderFactory.Ollama, 16384, 16384, System, Tools, Said()));

    [Fact]
    public void WithoutAKnownWindow_NothingIsSaid() =>
        Assert.Null(ContextManager.HeadShareNotice(Root(), "m", InferenceProviderFactory.Ollama, 0, 8192, System, Tools, Said()));

    /// <summary>The remedy that works on the person's server: on LM Studio, the setting when it is the limit, LM Studio's
    /// own load when it loaded the model smaller — a larger setting then changes nothing.</summary>
    [Fact]
    public void TheRemedy_IsTheServers()
    {
        Assert.Contains(Strings.HeadShareRemedyLmStudio,
            ContextManager.HeadShareNotice(Root(), "m", InferenceProviderFactory.LmStudio, 8192, 8192, System, Tools, Said()));
        Assert.Contains(Strings.HeadShareRemedyLmStudioLoaded(8192),
            ContextManager.HeadShareNotice(Root(), "m", InferenceProviderFactory.LmStudio, 8192, 32768, System, Tools, Said()));
        Assert.Contains(Strings.HeadShareRemedyServer,
            ContextManager.HeadShareNotice(Root(), "m", InferenceProviderFactory.OpenAiCompatible, 8192, 8192, System, Tools, Said()));
    }

    /// <summary>The largest part is named; over the whole window the refusal says it, not this notice.</summary>
    [Fact]
    public void TheLargestPart_IsNamed_AndAHeadOverTheWindow_IsLeftToTheRefusal()
    {
        Assert.Contains(Strings.ContextPartSystem(5000),
            ContextManager.HeadShareNotice(Root(), "m", InferenceProviderFactory.Ollama, 8192, 8192, 5000, 1000, Said()));
        Assert.Null(ContextManager.HeadShareNotice(Root(), "m", InferenceProviderFactory.Ollama, 4096, 4096, System, Tools, Said()));
    }

    /// <summary>Both front-ends ask, at the point they measure the window (the host serves VS Code and ACP).</summary>
    [Theory]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs")]
    public void BothFrontEnds_SayIt(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, Path.Combine(parts)));
        Assert.Contains("ContextManager.PrepareAsync(", code);                                     // WITNESS
        Assert.Contains("ContextManager.HeadShareNotice(", code);
    }
}
