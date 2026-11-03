using System.IO;
using System.Reflection;
using Inferpal.Services.Persistence;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a conversation holds besides its messages is declared once (<see cref="SessionState"/>): a session file keeps all
/// of it, and each front-end captures it in one place and applies it in one place. Applied field by field, a load left the
/// previous conversation's Plan mode, tools, step mode, X-Ray sections and plan file in place, and a start lost them.
/// </summary>
public sealed class SessionStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"session-state-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Every value of the record, each alone set to something a new conversation does not hold.</summary>
    internal static IEnumerable<(string Name, SessionState State)> EachValueAlone()
    {
        var parameters = typeof(SessionState).GetConstructors().Single(c => c.GetParameters().Length > 0).GetParameters();
        Assert.True(parameters.Length >= 8, "SessionState lost its values: this test reads nothing.");   // witness
        foreach (var p in parameters)
        {
            object value = p.ParameterType == typeof(bool) ? true
                         : p.ParameterType == typeof(string) ? $"{p.Name}-value"
                         : p.ParameterType == typeof(IReadOnlyList<string>) ? new List<string> { "pinned", "rules" }
                         : throw new InvalidOperationException($"SessionState.{p.Name}: a type this test cannot fill");
            var args = parameters.Select(q => q == p ? value : q.DefaultValue).ToArray();
            yield return (p.Name!, (SessionState)Activator.CreateInstance(typeof(SessionState), args)!);
        }
    }

    [Fact]
    public async Task EveryValue_SurvivesTheSessionFile()
    {
        var store = new ConversationStore(_dir);
        foreach (var (name, state) in EachValueAlone())
        {
            Assert.NotEqual(SessionState.New, state);
            await store.SaveAsync("held", [new("user", "q")], CancellationToken.None, workspaceRoot: null, state: state);
            Assert.True(state == (await store.LoadAsync("held", CancellationToken.None))!.State, $"{name} is not kept by a session file");
        }
    }

    /// <summary>A session saved by 1.8.9 holds the name, the template and the mode: it reads with them, and with a new
    /// conversation's values for the rest.</summary>
    [Fact]
    public async Task ASessionFromBeforeTheState_ReadsAsANewConversationsSwitches()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "old.json"), """
            {
              "saved_at": "2026-11-02T08:00:00Z",
              "messages": [ { "role": "user", "content": "q" }, { "role": "assistant", "content": "a" } ],
              "current_name": "feature-x",
              "template_suffix": "## Template",
              "mode": "Review"
            }
            """);

        var state = (await new ConversationStore(_dir).LoadAsync("old", CancellationToken.None))!.State;

        Assert.Equal(new SessionState(CurrentName: "feature-x", TemplateSuffix: "## Template", Mode: "Review"), state);
    }

    /// <summary>A new conversation's values are not written: a file saved with none of them is the file 1.8.9 wrote.</summary>
    [Fact]
    public async Task ANewConversationsValues_AreNotWritten()
    {
        var store = new ConversationStore(_dir);
        await store.SaveAsync("plain", [new("user", "q")], CancellationToken.None, workspaceRoot: null, state: SessionState.New);
        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "plain.json"));
        foreach (var key in new[] { "plan_mode", "tools_off", "step_mode", "xray_off", "active_plan", "mode", "template_suffix" })
            Assert.DoesNotContain($"\"{key}\"", json, StringComparison.Ordinal);
    }

    // ── Visual Studio: the view model cannot be built in the suite, its capture and apply are read on the source ──

    private static MethodDeclarationSyntax VsMethod(string name)
    {
        var path = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Modes.cs");
        var method = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot()
                                     .DescendantNodes().OfType<MethodDeclarationSyntax>()
                                     .SingleOrDefault(m => m.Identifier.Text == name);
        Assert.True(method is not null, $"InferpalToolWindowData.{name} moved: this test reads nothing.");   // witness
        return method!;
    }

    /// <summary>A value added to the record with a default compiles everywhere and is captured nowhere: the capture
    /// names every value, the apply reads every value.</summary>
    [Fact]
    public void VisualStudio_CapturesAndAppliesEveryValue()
    {
        var parameters = typeof(SessionState).GetConstructors().Single(c => c.GetParameters().Length > 0).GetParameters();

        var created = VsMethod("CaptureState").DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>().Single();
        Assert.Equal(parameters.Length, created.ArgumentList.Arguments.Count);

        var apply = VsMethod("ApplyState").Body!.ToString();
        foreach (var p in parameters)
        {
            var property = char.ToUpperInvariant(p.Name![0]) + p.Name[1..];
            Assert.True(apply.Contains($"state.{property}", StringComparison.Ordinal), $"ApplyState does not read {property}");
        }
    }

    /// <summary>Every save and every restore of the view model goes through the capture and the apply: none passes the
    /// values one by one.</summary>
    [Fact]
    public void VisualStudio_SavesAndRestores_OnlyThroughTheState()
    {
        var dir = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow");
        var sources = Directory.GetFiles(dir, "InferpalToolWindowData*.cs").Select(ConventionCoverageTests.CodeOnly).ToList();
        Assert.True(sources.Count > 10, "the view model's partials moved: this test reads nothing.");   // witness
        var all = string.Join("\n", sources);
        Assert.DoesNotContain("templateSuffix:", all, StringComparison.Ordinal);
        Assert.DoesNotContain("currentName:", all, StringComparison.Ordinal);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(all, @"RestoreConversation\([^;]*,\s*[^;]*State\b").Count >= 4,
                    "a restore no longer passes a SessionState");
    }

    // ── VS Code: a loaded conversation's state becomes the window's mirrors ──

    [Fact]
    public void VsCode_FollowsTheLoadedConversationsState()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var apply = WebviewRebuildTests.Body(provider, "private applySession(");
        Assert.Contains("this.planMode = loaded.planMode === true;", apply, StringComparison.Ordinal);
        Assert.Contains("this.switches = loaded.switches ??", apply, StringComparison.Ordinal);
        // A load passes its result: a branch switched to, a session picked; the start passes it unless the person chose
        // something meanwhile (ARestore_NeverTakesBackWhatThePersonChose).
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            provider, @"this\.applySession\((\w+)\.messages, \1\.nextTurnTokens \?\? 0, \1\)").Count);
        // ⚠ The restart does not: the window's mirrors were just given to the host, and one it refused stays for its next
        // start — taken from the answer, it was the host's default, and the slot recorded it.
        Assert.Contains("this.applySession(back.messages, back.nextTurnTokens ?? 0);", provider, StringComparison.Ordinal);
    }

    /// <summary>Plan runs the agent loop in Visual Studio too, as on the host (<c>HostServer.TurnIsAgent</c>).</summary>
    [Fact]
    public void VisualStudio_RunsTheAgentLoopInPlan()
    {
        var turn = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));
        Assert.Contains("var useOrchestrator = (_config.AgentModeEnabled || _planMode)", turn, StringComparison.Ordinal);
    }

    /// <summary>A restore replaces the whole state: what the person set before it ran (Plan, a switch, a plan to follow)
    /// keeps the restore out in Visual Studio, and is given back to the host in VS Code.</summary>
    [Fact]
    public void ARestore_NeverTakesBackWhatThePersonChose()
    {
        var connection = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"));
        var asked = connection[connection.IndexOf("private bool AskedOnScreen()", StringComparison.Ordinal)..];
        Assert.Contains("CaptureState() with { CurrentName = null } != SessionState.New", asked[..asked.IndexOf(';')],
                        StringComparison.Ordinal);

        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("this.applySession(last.messages, last.nextTurnTokens ?? 0, chosen ? undefined : last);", provider,
                        StringComparison.Ordinal);
        Assert.Contains("await this.giveBackChoices(host, last);", provider, StringComparison.Ordinal);
        var giveBack = WebviewRebuildTests.Body(provider, "private async giveBackChoices(");
        Assert.Contains("host.planMode(this.planMode)", giveBack, StringComparison.Ordinal);
        Assert.Contains("host.sessionSwitches(this.switches)", giveBack, StringComparison.Ordinal);
        // Refused, the mirrors show what the host holds: never Plan over a host that runs with the write tools.
        var refused = giveBack[giveBack.IndexOf("catch (err)", StringComparison.Ordinal)..];
        Assert.Contains("this.planMode = loaded.planMode === true;", refused, StringComparison.Ordinal);
        Assert.Contains("this.switches = loaded.switches ?? noSwitches();", refused, StringComparison.Ordinal);
        Assert.Contains("await this.refreshModes();", giveBack, StringComparison.Ordinal);
    }
}
