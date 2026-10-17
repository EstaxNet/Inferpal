using System.IO;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The head of every request does not follow the active file. A server that reuses the cache of the request's prefix
/// (llama.cpp, LM Studio, Ollama, vLLM) reads again everything after the first character that changed: with the persona,
/// the folders' <c>AGENTS.md</c>, the instructions and rules scoped by glob in the system message, opening a file of
/// another language or folder made it read the whole conversation again. They ride with the question
/// (<see cref="ActiveFileContext"/>), sent when the history does not already carry them.
/// </summary>
public sealed class PromptPrefixStabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"prefix-{Guid.NewGuid():N}");

    public PromptPrefixStabilityTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write("AGENTS.md", "ROOT-AGENTS-FOR-EVERY-QUESTION");
        Write(Path.Combine("src", "Api", "AGENTS.md"), "API-FOLDER-AGENTS");
        Write(Path.Combine(".github", "instructions", "cs.instructions.md"), "---\napplyTo: \"**/*.cs\"\n---\nCSHARP-ONLY-INSTRUCTION");
        Write(Path.Combine(".inferpal", "rules", "py.md"), "---\ndescription: Py\nglobs: **/*.py\n---\nPYTHON-ONLY-RULE");
        Write(Path.Combine(".inferpal", "rules", "always.md"), "---\ndescription: Always\nalwaysApply: true\n---\nALWAYS-RULE");
        Write(Path.Combine("src", "Api", "Orders.cs"), "class Orders {}");
        Write(Path.Combine("tools", "report.py"), "print('report')");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort teardown */ }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // A pinned file larger than any share at the default window: its part is what the budget decides.
    private InferpalConfig Config()
    {
        var pin = Path.Combine(_root, "notes.txt");
        File.WriteAllText(pin, string.Concat(Enumerable.Range(0, 2_000).Select(i => $"note {i}. ")));
        return new InferpalConfig { PersonaAutoSwitch = true, PinnedContextFiles = pin, ContextWindowSize = 8_192 };
    }

    private string System(SystemPromptBuilder builder, string? relative) => builder.Build(
        "BASE", language: relative is null ? null : SystemPromptBuilder.LanguageOf(relative), projectRoot: _root,
        activeFileRelPath: relative, activeFilePath: relative is null ? null : Path.Combine(_root, relative));

    private string Block(SystemPromptBuilder builder, string? relative) => builder.ActiveFileBlock(
        language: relative is null ? null : SystemPromptBuilder.LanguageOf(relative), projectRoot: _root,
        activeFileRelPath: relative, activeFilePath: relative is null ? null : Path.Combine(_root, relative));

    [Fact]
    public void TheSystemMessage_IsTheSameToTheByte_WhenOnlyTheActiveFileChanges()
    {
        var builder = new SystemPromptBuilder(Config(), workspaceRoot: _root);

        var forCs   = System(builder, "src/Api/Orders.cs");
        var forPy   = System(builder, "tools/report.py");
        var forNone = System(builder, null);

        Assert.Contains("ROOT-AGENTS-FOR-EVERY-QUESTION", forCs);   // witness: the repository's root instructions stay
        Assert.Contains("ALWAYS-RULE", forCs);
        Assert.Contains("truncated to", forCs);                     // witness: the pin is cut, so its share decides
        Assert.Equal(forCs, forPy);
        Assert.Equal(forCs, forNone);
    }

    [Fact]
    public void WhatFollowsTheActiveFile_RidesWithTheQuestion()
    {
        var builder = new SystemPromptBuilder(Config(), workspaceRoot: _root);

        var cs = Block(builder, "src/Api/Orders.cs");
        var py = Block(builder, "tools/report.py");

        Assert.StartsWith("## Active file: src/Api/Orders.cs (csharp)", cs, StringComparison.Ordinal);
        Assert.Contains("API-FOLDER-AGENTS", cs);
        Assert.Contains("CSHARP-ONLY-INSTRUCTION", cs);
        Assert.Contains(SystemPromptBuilder.PersonaSnippetFor("csharp"), cs);
        Assert.DoesNotContain("PYTHON-ONLY-RULE", cs);
        Assert.DoesNotContain("ROOT-AGENTS-FOR-EVERY-QUESTION", cs);   // in the system message, not twice

        Assert.StartsWith("## Active file: tools/report.py (python)", py, StringComparison.Ordinal);
        Assert.Contains("PYTHON-ONLY-RULE", py);
        Assert.Contains(SystemPromptBuilder.PersonaSnippetFor("python"), py);
        Assert.DoesNotContain("API-FOLDER-AGENTS", py);
        Assert.DoesNotContain("ALWAYS-RULE", py);

        Assert.Equal(string.Empty, Block(builder, null));
    }

    [Fact]
    public void TheBlock_GoesRightBeforeTheRequest()
    {
        var question = ChatTurnPolicy.WithInjectedContext("What does it do?", "## Relevant code\n\nx", null, "## Active file: a.cs");

        Assert.EndsWith("## Active file: a.cs\n\n" + ChatTurnPolicy.UserRequestHeader + "\n\nWhat does it do?", question,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverLongScopedRule_IsCutInTheBlock_AndSaysSo()
    {
        Write(Path.Combine(".inferpal", "rules", "big.md"),
              "---\ndescription: Big\nglobs: **/*.py\n---\n" + string.Concat(Enumerable.Range(0, 3_000).Select(i => $"step {i}. ")));
        var builder = new SystemPromptBuilder(Config(), workspaceRoot: _root);

        var block = Block(builder, "tools/report.py");

        Assert.Contains("truncated to", block);
        Assert.True(block.Length < 8_192, $"the block is {block.Length} characters");
    }

    // ── When the block is sent ────────────────────────────────────────────────

    private static ChatMessageDto Asked(string? block, string text = "q") =>
        new("user", ChatTurnPolicy.WithInjectedContext(text, block));

    [Fact]
    public void TheBlock_IsSentWhenTheHistoryDoesNotCarryIt_AndOnlyThen()
    {
        const string cs = "## Active file: a.cs (csharp)\n\nC# things";
        const string py = "## Active file: b.py (python)\n\nPython things";
        var history = new List<ChatMessageDto> { new("system", "s") };

        Assert.Equal(cs, ActiveFileContext.ToSend(cs, history));            // the first question
        history.Add(Asked(cs));
        history.Add(new("assistant", "a"));
        Assert.Null(ActiveFileContext.ToSend(cs, history));                 // same file: already said
        Assert.Equal(py, ActiveFileContext.ToSend(py, history));            // another file
        history.Add(Asked(py));
        history.Add(new("assistant", "a"));
        Assert.Equal(cs, ActiveFileContext.ToSend(cs, history));            // back to the first: the last block is py's

        // Regenerate takes the last question back, block included: the next one sends it again.
        history.RemoveRange(history.Count - 2, 2);
        Assert.Equal(py, ActiveFileContext.ToSend(py, history));
        // A compaction that dropped every question carrying a block: sent again.
        Assert.Equal(py, ActiveFileContext.ToSend(py, [new("system", "s"), new("user", "[Context Summary] …")]));
    }

    [Fact]
    public void AFileClosedAfterABlock_SaysSoOnce_AndNoFileEverSaysNothing()
    {
        const string cs = "## Active file: a.cs\n\nC# things";
        List<ChatMessageDto> history = [new("system", "s"), Asked(cs), new("assistant", "a")];

        Assert.Equal(ActiveFileContext.NoneBlock, ActiveFileContext.ToSend(string.Empty, history));
        history.Add(Asked(ActiveFileContext.NoneBlock));
        Assert.Null(ActiveFileContext.ToSend(string.Empty, history));

        // An agent with no editor (ACP): no file ever, no block ever.
        Assert.Null(ActiveFileContext.ToSend(string.Empty, [new("system", "s"), new("user", "q")]));
    }

    [Fact]
    public void TheLastBlock_IsReadWhereverTheQuestionPutsIt()
    {
        var question = ChatTurnPolicy.WithInjectedContext("q", "## Relevant code\n\nx", "## Workspace context\n\ny",
                                                          "## Active file: a.cs\n\n## Rules\n\nr");

        Assert.Equal("## Active file: a.cs\n\n## Rules\n\nr", ActiveFileContext.LastIn([new("user", question)]));
    }

    // ── X-Ray ───────────────────────────────────────────────────────────────────

    [Fact]
    public void XRay_ListsTheBlocksLayers_WithTheQuestion_OutOfTheSystemPrompt_AndCountsThemOnce()
    {
        var builder  = new SystemPromptBuilder(Config(), workspaceRoot: _root);
        var sections = builder.BuildSections("BASE", "python", null, _root, "tools/report.py", null,
                                             Path.Combine(_root, "tools", "report.py"));

        var toSend = XRayPanelPresenter.Build(sections, null, 0, 8_192);
        var sent   = XRayPanelPresenter.Build(sections, null, 0, 8_192, questionLayersInHistory: true);

        var rule = Assert.Single(toSend.Sections, s => s.Content.Contains("PYTHON-ONLY-RULE", StringComparison.Ordinal));
        Assert.EndsWith(Inferpal.Localization.Strings.XrayWithTheQuestion, rule.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("PYTHON-ONLY-RULE", toSend.RawPrompt);
        Assert.Contains("ALWAYS-RULE", toSend.RawPrompt);
        Assert.Equal(toSend.TotalTokens - rule.Tokens - toSend.Sections.Where(s => s.Id == "Persona").Sum(s => s.Tokens),
                     sent.TotalTokens);
    }

    // ── Background tasks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ATask_CarriesTheActiveFileOfItsSubmission_NotTheOneOpenWhenItRuns()
    {
        var context = "## Active file: a.cs";
        var idle    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen    = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new BackgroundTaskQueue(
            (task, _, _) => { seen.TrySetResult(task.Context); return Task.FromResult(BackgroundTaskQueue.TaskRunOutcome.Of("done")); },
            waitForChatIdle: _ => idle.Task,
            submissionContext: () => context);

        Assert.NotNull(queue.Submit("Explain this file"));
        context = "## Active file: b.py";      // the user opened another file while the task waited
        idle.SetResult();

        Assert.Equal("## Active file: a.cs", await seen.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void BothEditors_SendATasksContext_BeforeItsObjective()
    {
        var root = ConventionCoverageTests.RepoRoot();
        foreach (var file in new[] { Path.Combine("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"),
                                     Path.Combine("Inferpal.Host", "HostSlashCommands.cs") })
            Assert.Contains("WithInjectedContext(task.Objective, task.Context)",
                            ConventionCoverageTests.CodeOnly(Path.Combine(root, file)), StringComparison.Ordinal);
    }

    // ── The session summary ───────────────────────────────────────────────────────

    [Fact]
    public void BothEditors_PutTheSessionSummaryInTheSystemPrompt_OnlyAtTheCompaction()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var vm   = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostServer.cs"));

        // The writer of the system message reads the summary frozen at the compaction, never the newest one.
        Assert.Contains("\"\\n\\n## Session Summary\\n\\n\" + _recapInSystem", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("\"\\n\\n## Session Summary\\n\\n\" + _oodaSummary", vm, StringComparison.Ordinal);
        Assert.Contains("\"\\n\\n## Session Summary\\n\\n\" + s.RecapInSystem", host, StringComparison.Ordinal);
        Assert.DoesNotContain("\"\\n\\n## Session Summary\\n\\n\" + s.OodaSummary", host, StringComparison.Ordinal);
        // And the compaction is the one place it is taken.
        Assert.Contains("ApplyTruncation(_history,decision.Plan);_recapInSystem=_oodaSummary;",
                        string.Concat(vm.Where(c => !char.IsWhiteSpace(c))), StringComparison.Ordinal);
        Assert.Contains("ApplyTruncation(s.History,ctxDecision.Plan);s.RecapInSystem=s.OodaSummary;",
                        string.Concat(host.Where(c => !char.IsWhiteSpace(c))), StringComparison.Ordinal);
    }

    // ── What the reviews found ────────────────────────────────────────────────────

    [Fact]
    public void AQuestionWhoseBlockACompactionDropped_GetsItBack()
    {
        const string cs = "## Active file: a.cs (csharp)\n\nC# things";
        // The block went with question 1; questions 2 and 3, about the same file, did not carry it again.
        List<ChatMessageDto> history = [new("system", "s"), new("user", "[Context Summary] question 1 and its answer"),
                                         new("assistant", "a2"), new("user", "question 3")];

        Assert.True(ActiveFileContext.Restore(history, cs));

        Assert.Equal(cs + "\n\n" + ChatTurnPolicy.UserRequestHeader + "\n\nquestion 3", history[^1].Content);
        Assert.False(ActiveFileContext.Restore(history, cs));   // carried now: nothing to do
    }

    [Fact]
    public void ABlockBudgetedForAWindowMeasuredSmallerSince_IsBuiltAgainInTheQuestion()
    {
        const string wide = "## Active file: a.cs\n\nlong rules";
        const string fit  = "## Active file: a.cs\n\nshort";
        List<ChatMessageDto> history = [new("system", "s"),
                                        new("user", ChatTurnPolicy.WithInjectedContext("q", "## Relevant code\n\nx", wide))];

        Assert.True(ActiveFileContext.Restore(history, fit));

        Assert.Equal(ChatTurnPolicy.WithInjectedContext("q", "## Relevant code\n\nx", fit), history[^1].Content);
    }

    [Fact]
    public void ThePersona_GoesWithAnOpenFile_AndOnlyThen()
    {
        var builder = new SystemPromptBuilder(new InferpalConfig { PersonaAutoSwitch = true });

        Assert.DoesNotContain(builder.BuildSections("BASE", language: "csharp"), s => s.Kind == PromptSectionKind.Persona);
        Assert.Contains(builder.BuildSections("BASE", language: "csharp", activeFileRelPath: "a.cs"),
                        s => s.Kind == PromptSectionKind.Persona);   // reference arm
    }

    [Fact]
    public void ARootFileImportingAFoldersAgents_KeepsTheSystemMessageStill()
    {
        Write("CLAUDE.md", "ROOT-CLAUDE\n\n@src/Api/AGENTS.md\n");
        var builder = new SystemPromptBuilder(Config(), workspaceRoot: _root);

        var inTheFolder = System(builder, "src/Api/Orders.cs");
        var elsewhere   = System(builder, "tools/report.py");

        Assert.Contains("ROOT-CLAUDE", inTheFolder);   // witness
        Assert.Equal(elsewhere, inTheFolder);
    }

    [Fact]
    public void TheBlock_TakesWhatTheSystemMessagesFilesLeave()
    {
        var folder = string.Concat(Enumerable.Range(0, 700).Select(i => $"rule {i}. "));
        Write(Path.Combine("src", "Api", "AGENTS.md"), folder);
        var builder = new SystemPromptBuilder(new InferpalConfig { ContextWindowSize = 8_192 }, workspaceRoot: _root);

        var block = Block(builder, "src/Api/Orders.cs");

        Assert.True(folder.Length > 8_192 / SystemPromptBuilder.QuestionShareDivisor, "too short to be cut at a quarter");   // witness
        Assert.Contains(folder.Trim(), block);
        Assert.DoesNotContain("truncated to", block);
    }

    [Fact]
    public void SlashXray_CountsABlockTheHistoryCarries_Once_LikeThePanel()
    {
        var sections = new SystemPromptBuilder(Config(), workspaceRoot: _root).BuildSections(
            "BASE", "python", null, _root, "tools/report.py", null, Path.Combine(_root, "tools", "report.py"));

        var toSend  = Inferpal.Services.Commands.XRayCommandHandler.Handle(sections, 0, 8_192, Config());
        var carried = Inferpal.Services.Commands.XRayCommandHandler.Handle(sections, 0, 8_192, Config(), questionLayersInHistory: true);
        var panel   = XRayPanelPresenter.Build(sections, null, 0, 8_192, questionLayersInHistory: true);

        Assert.NotEqual(toSend, carried);
        Assert.Contains(Inferpal.Localization.Strings.XrayHeader($"~{panel.TotalTokens:N0}"), carried);
    }

    [Fact]
    public void WhatTheUserTyped_IsNeverReadAsABlock()
    {
        Assert.Null(ActiveFileContext.LastIn([new("user", "## Active file: notes.md\n\nmy own heading")]));
        Assert.Null(ActiveFileContext.LastIn(
            [new("user", ChatTurnPolicy.WithInjectedContext("quoting:\n\n## Active file: x", "## Relevant code\n\ny"))]));
    }

    [Fact]
    public void AFileNothingAppliesTo_SaysSoOnceAfterAFileThatHadInstructions_AndOtherwiseNothing()
    {
        var builder = new SystemPromptBuilder(new InferpalConfig { PersonaAutoSwitch = false }, workspaceRoot: _root);
        var bare    = Block(builder, "README.md");
        var cs      = Block(builder, "src/Api/Orders.cs");
        List<ChatMessageDto> history = [new("system", "s")];

        Assert.Null(ActiveFileContext.ToSend(bare, history));               // nothing ever said: nothing to undo
        history.Add(Asked(cs));
        Assert.Equal(bare, ActiveFileContext.ToSend(bare, history));        // the C# folder's no longer hold
        history.Add(Asked(bare));
        Assert.Null(ActiveFileContext.ToSend(Block(builder, "docs.md"), history));
        Assert.Null(ActiveFileContext.ToSend(string.Empty, history));        // closed: nothing held already
        Assert.EndsWith(ModelPrompts.NoFileInstructions, bare, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScopedRules_AreAnXRayRowOfTheirOwn()
    {
        var sections = new SystemPromptBuilder(Config(), workspaceRoot: _root).BuildSections(
            "BASE", "python", null, _root, "tools/report.py", null, Path.Combine(_root, "tools", "report.py"));

        var rules = sections.Where(s => s.Kind == PromptSectionKind.Rules).Select(XRayPanelPresenter.SectionId).ToList();

        Assert.Equal(2, rules.Count);   // witness: the always rule and the glob rule
        Assert.Equal(2, rules.Distinct().Count());
    }

    [Fact]
    public async Task ATddFixRequest_CarriesTheActiveFileBlock()
    {
        var asked  = new List<string>();
        var client = new FakeInferenceProvider
        {
            RunAgentThroughChat = true,
            OnChatRequest = (_, history, _, _) =>
            {
                lock (asked) asked.Add(history.Last(m => m.Role == "user").Content);
                return Task.FromResult(new ChatTurnResult("fixed", null, 1, 1));
            },
        };
        var tests = new Queue<string>(["✗ FAILED — Failed: 1, Passed: 4, Skipped: 0, Total: 5\n\nFailing tests:\n  ✗ MyTest",
                                       "✓ PASSED — Failed: 0, Passed: 5, Skipped: 0, Total: 5"]);

        await Inferpal.Services.Commands.TddCommandHandler.HandleAsync(
            client, new InferpalConfig(), new ScriptedTests(tests), "s", ["/tdd"], _root,
            null, null, null, null, null, CancellationToken.None, activeFileContext: "## Active file: a.cs\n\nA-CS-RULE");

        lock (asked) Assert.Contains(asked, q => q.StartsWith("## Active file: a.cs\n\nA-CS-RULE", StringComparison.Ordinal));
    }

    private sealed class ScriptedTests(Queue<string> outputs) : Inferpal.Services.Execution.IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, System.Text.Json.JsonElement args, CancellationToken ct) =>
            Task.FromResult(outputs.Count > 0 ? outputs.Dequeue() : "✓ PASSED — Failed: 0, Passed: 5, Skipped: 0, Total: 5");
    }

    [Fact]
    public void VisualStudiosFixBuild_CarriesTheActiveFileBlock()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow",
                                                               "InferpalToolWindowData.PromptHistory.cs"));

        Assert.Contains("WithInjectedContext(BuildFixPrompt(buildOutput), activeFile)", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void BothEditors_PutTheBlockBack_AfterTheMeasureAndAfterTheCompaction()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var vm   = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostServer.cs"));

        Assert.Equal(2, Count(vm, "ActiveFileContext.Restore(_history, BuildActiveFileBlock());"));
        Assert.Equal(2, Count(host, "ActiveFileContext.Restore(s.History, BuildActiveFileBlock(s));"));

        static int Count(string code, string call) => (code.Length - code.Replace(call, string.Empty).Length) / call.Length;
    }
}
