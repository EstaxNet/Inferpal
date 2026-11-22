using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The tools assistant in Visual Studio's settings window (pari 163) reads and writes through the chat window: what it
/// opens on is what the settings describe, Apply writes <c>disabledTools</c> and the tool is no longer offered, and opening
/// again shows what was written. Windows only: the window is Visual Studio's.
/// </summary>
[Collection(SignalCollection.Name)]   // the window writes the active solution's signal
public sealed class ToolsAssistantVsTests
{
    [Fact]
    public async Task Apply_WritesTheSetting_AndTheAssistantOpensOnWhatItWrote()
    {
        var config = new InferpalConfig { DefaultModel = "m", DisabledTools = "" };
        using var vs = new VsWindowHarness(config: config);
        var assist = Assert.IsType<Func<AssistantAnswers?, bool, Task<(ToolsAssistantModel, string?, bool)>>>(vs.Holder.ToolsAssist);
        bool Offered(string tool) => vs.Tools.Definitions.Any(d => d.Function.Name == tool);

        var (opened, _, wroteOnOpen) = await assist(null, false);
        Assert.False(wroteOnOpen);
        Assert.Equal(AssistantCommands.Free, opened.Answers.Commands);
        Assert.True(Offered("run_command"));                                  // witness: on before Apply

        var (applied, said, wrote) = await assist(opened.Answers with { Commands = AssistantCommands.Nothing }, true);
        Assert.True(wrote);
        Assert.Equal(Localization.Strings.AssistApplied(applied.Plan.On, applied.Plan.Off), said);
        Assert.Contains("run_command", config.SharedDisabledTools.Split('\n'));
        Assert.False(Offered("run_command"));

        var (again, _, _) = await assist(null, false);
        Assert.Equal(AssistantCommands.Nothing, again.Answers.Commands);
        Assert.Empty(again.Rows);                                             // opened on what is: nothing to change
    }
}
