using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Commands;

/// <summary>What became of one target of <c>/test coverage</c>.</summary>
internal enum CoverageVerdict
{
    Kept, NoPass, NoAssertion, Skips, NothingNew, NotMeasured, ModelSilent, ModelCut, ModelFailed, NoFile, Declined,
    NotWritten, Failed, NotATestPath,
}

/// <param name="TestFile">The new test file, relative to the root.</param>
/// <param name="NewLines">Lines it covers that nothing covered before it.</param>
/// <param name="Cause">What a failed request to the model answered (<see cref="CoverageVerdict.ModelFailed"/>).</param>
/// <param name="StillThere">Set aside, but still in the workspace: its undo could not remove it.</param>
internal sealed record CoverageTargetOutcome(CoverageTarget Target, string TestFile, CoverageVerdict Verdict, int NewLines,
                                             string? Cause = null, bool StillThere = false)
{
    /// <summary>Whether the targets after this one are left: the person refused a write, the workspace refuses it (a rule,
    /// the confinement), or the model cannot be reached — the next one would end the same way.</summary>
    public bool StopsTheRun => Verdict is CoverageVerdict.Declined or CoverageVerdict.NotWritten or CoverageVerdict.ModelFailed;
}

/// <summary>
/// <c>/test coverage [folder]</c>: the coverage measured by the tool the project has, the methods no test reaches chosen
/// without the model, then one target at a time — a new test file asked of the model with a neighbouring test as an
/// example, kept only if it passes three times, asserts, skips nothing and covers lines nothing covered.
/// </summary>
/// <remarks>
/// ⚠ The model gets no tool: it returns the content of ONE new file, which this command writes through
/// <c>write_file</c> (the approval card, the run). So no production file, and no existing test, can be changed — a path
/// that is not a new test file is refused before the card. A file set aside is given back by the run's own undo
/// (<see cref="FileHistoryService.UndoFileAsync"/>): removed, its copy kept.
/// </remarks>
internal static class TestCoverageCommandHandler
{
    /// <summary>A target the model has not answered in this time is lost, and said; never retried.</summary>
    internal static readonly TimeSpan ModelDeadline = TimeSpan.FromMinutes(8);

    private const int SourceBudget = 12_000, ExampleBudget = 8_000, FailureTail = 4_000, ExamplesScanned = 400, TargetLines = 300;

    /// <param name="parts">Tokenised command: <c>/test coverage [folder | project | file]</c>.</param>
    /// <param name="root">The workspace the tools write in; empty when none is open.</param>
    /// <param name="detect">The ecosystem of a folder; tests give a fake one.</param>
    public static async Task<string> HandleAsync(
        IInferenceProvider client, InferpalConfig config, IToolRegistry tools, FileHistoryService history,
        string[] parts, string? root, Action<string>? onProgress, CancellationToken ct,
        Func<string, string, CancellationToken, Task<(ICoverageEcosystem? Ecosystem, string? Notice)>>? detect = null)
    {
        if (string.IsNullOrEmpty(root)) return Tools.PathSanitizer.NoWorkspaceRoot;
        var asked  = parts.Length > 2 ? string.Join(' ', parts[2..]).Trim().Trim('"') : string.Empty;
        // The folder measured, and the part of it whose methods are targets: a source file is measured with its folder
        // (no tool measures one file), a project is its folder.
        string folder = string.Empty, scope = string.Empty;
        if (asked.Length > 0)
        {
            var full = Path.GetFullPath(Path.IsPathFullyQualified(asked) ? asked : Path.Combine(root, asked));
            var isFile = File.Exists(full);
            if (!Tools.PathSanitizer.IsUnderRoot(full, root) || !(Directory.Exists(full) || isFile))
                return Strings.CoverageNoSuchFolder(asked);
            var measured = isFile ? Path.GetDirectoryName(full)! : full;
            folder = Relative(root, measured);
            scope  = isFile && !full.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && !SolutionFiles.IsSolution(full)
                ? Relative(root, full) : folder;
        }

        onProgress?.Invoke(Strings.CoverageDetecting);
        var (ecosystem, notice) = await (detect ?? CoverageEcosystems.DetectAsync)(root, folder, ct);
        if (ecosystem is null) return notice ?? Strings.CoverageRunnerUnsupported("?");

        // The measures' reports, outside the workspace: nothing but the new tests is written there.
        var work = Path.Combine(Path.GetTempPath(), "inferpal-coverage", Guid.NewGuid().ToString("N"));
        try
        {
            return await RunAsync(client, config, tools, history, ecosystem, root, scope, work, onProgress, ct);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* cleanup */ }
        }
    }

    private static async Task<string> RunAsync(
        IInferenceProvider client, InferpalConfig config, IToolRegistry tools, FileHistoryService history,
        ICoverageEcosystem ecosystem, string root, string folder, string work, Action<string>? onProgress, CancellationToken ct)
    {
        var where = folder.Length == 0 ? Strings.CoverageWorkspace : $"`{folder}`";
        var header = Strings.CoverageHeader(ecosystem.Name, where);

        onProgress?.Invoke(Strings.CoverageMeasuringBefore(ecosystem.Name));
        var before = await ecosystem.MeasureAsync(null, Path.Combine(work, "before"), ct);
        if (before.Report is not { } baseline)
            return $"{header}\n\n{Strings.CoverageNoReport(ecosystem.Name)}\n\n```\n{Ends(before.Output, 1_200, 1_800)}\n```";

        // ⚠ What the workspace keeps from AI tools is neither a target nor an example: the model would read it.
        var excluded = baseline.Files.Keys.Where(f => CoverageTargets.InFolder(f, folder)
                           && Governance.ContentExclusions.NoticeFor(Path.Combine(root, f), null) is not null)
                       .ToHashSet(StringComparer.Ordinal);
        var all = CoverageTargets.Rank(baseline, folder, f => excluded.Contains(f) ? [] : ReadLines(root, f))
                                 .Where(t => !excluded.Contains(t.File)).ToList();
        var files = baseline.Files.Keys.Where(f => CoverageTargets.InFolder(f, folder) && !excluded.Contains(f)).ToList();
        var (covered0, total0) = baseline.Of(files);
        // A folder of tests (or only generated files): measured, and nothing in it is a target — said, not "0 %".
        if (files.Count == 0 && excluded.Count == 0 && folder.Length > 0)
            return $"{header}\n\n{Strings.CoverageNoSourceInScope($"`{folder}`")}";
        if (all.Count == 0)
            return $"{header}\n\n{Strings.CoverageNothingToCover(Percent(covered0, total0))}"
                 + (excluded.Count > 0 ? "\n" + Strings.CoverageExcluded(excluded.Count) : string.Empty);

        var taken    = all.Take(CoverageTargets.MaxPerRun).ToList();
        var covered  = files.ToDictionary(f => f, f => baseline.Files[f].Where(l => l.Value > 0).Select(l => l.Key).ToHashSet(),
                                          StringComparer.Ordinal);
        var model    = ModelRouter.Resolve(config, ModelRole.Agent);
        var outcomes = new List<CoverageTargetOutcome>();
        var stopped  = false;
        // One run for the whole command: /undo-run gives back every test it kept.
        var runId = history.BeginRun();
        try
        {
            var run = history.Runs.First(r => r.Id == runId);
            for (var i = 0; i < taken.Count && !stopped; i++)
            {
                onProgress?.Invoke(Strings.CoverageTargetProgress(i + 1, taken.Count, taken[i].Name));
                try
                {
                    var outcome = await TargetAsync(client, model, tools, history, run, ecosystem, root, taken[i], covered,
                                                    Path.Combine(work, $"t{i + 1}"), ct);
                    outcomes.Add(outcome);
                    if (outcome.StopsTheRun) break;
                }
                // ⚠ Stopped: the tests kept so far are said — without the report, nobody knows which files stayed.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { stopped = true; }
                // Before any file is written (its path, its prompt): this target only, said with its cause.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Diagnostics.Swallow("TestCoverage.Target", ex);
                    outcomes.Add(new(taken[i], string.Empty, CoverageVerdict.Failed, 0, Diagnostics.RootMessage(ex)));
                }
            }
        }
        finally
        {
            history.EndRun();
        }

        CoverageRun? after = before;
        if (!stopped && outcomes.Any(o => o.Verdict == CoverageVerdict.Kept))
        {
            onProgress?.Invoke(Strings.CoverageMeasuringAfter);
            // ⚠ A Stop during the last measure (the whole suite, minutes) still reports which files stayed.
            try { after = await ecosystem.MeasureAsync(null, Path.Combine(work, "after"), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { stopped = true; }
        }
        // Each kept test passed alone: the suite run after says whether they pass together.
        var brokenAfter = !stopped && !ReferenceEquals(after, before) && before.Passed && after is { Passed: false };
        var report = Report(header, baseline, stopped ? null : after, files, outcomes, all.Count - outcomes.Count,
                            excluded.Count, before.Passed, brokenAfter);
        return stopped ? report + "\n\n" + Strings.CoverageStopped(outcomes.Count, taken.Count) : report;
    }

    private static async Task<CoverageTargetOutcome> TargetAsync(
        IInferenceProvider client, string model, IToolRegistry tools, FileHistoryService history, HistoryRun run,
        ICoverageEcosystem ecosystem, string root, CoverageTarget target, Dictionary<string, HashSet<int>> covered,
        string work, CancellationToken ct)
    {
        var source   = Path.Combine(root, target.File);
        var testPath = ecosystem.NewTestPath(source);
        var relative = Path.GetRelativePath(root, testPath).Replace('\\', '/');
        CoverageTargetOutcome Outcome(CoverageVerdict verdict, int lines = 0, string? cause = null) =>
            new(target, relative, verdict, lines, cause);

        // ⚠ A new file, a test file, in the workspace — refused before the card, never asked about.
        if (!IsNewTestFile(testPath, root)) return Outcome(CoverageVerdict.NotATestPath);

        var messages = new List<ChatMessageDto>
        {
            new("system", CodeActions.TestGenerationPrompts.CoverageSystem),
            new("user", Prompt(ecosystem, root, target, testPath, relative)),
        };
        var (reply, failure, cause) = await AskAsync(client, model, messages, ct);
        if (reply is null) return Outcome(failure, cause: cause);
        var content = CodeOf(reply);
        if (content.Trim().Length == 0) return Outcome(CoverageVerdict.NoFile);
        try
        {
            // Not written as sent — refused, or other bytes landed: whatever is there leaves with it.
            if (await WriteAsync(tools, testPath, content, ct) is { } notWritten)
                return await SetAsideAsync(Outcome(notWritten.Verdict, cause: notWritten.Cause));

            var (passed, output) = await PassesThreeTimesAsync(ecosystem, testPath, ct);
            if (!passed)
            {
                // One repair, with the end of what failed.
                messages.Add(new("assistant", reply));
                messages.Add(new("user", $"Running that file failed:\n```\n{Tail(output, FailureTail)}\n```\n"
                                         + CodeActions.TestGenerationPrompts.CoverageRepair));
                if ((await AskAsync(client, model, messages, ct)).Reply is { } repaired && CodeOf(repaired) is var fixedContent
                    && fixedContent.Trim().Length > 0)
                {
                    // Read in this run, so write_file replaces the file it wrote rather than refusing an unread one.
                    await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = testPath }), ct);
                    if (await WriteAsync(tools, testPath, fixedContent, ct) is { } repairNotWritten)
                        return await SetAsideAsync(Outcome(repairNotWritten.Verdict, cause: repairNotWritten.Cause));
                    content = fixedContent;
                    (passed, _) = await PassesThreeTimesAsync(ecosystem, testPath, ct);
                }
            }

            CoverageVerdict? rejected = !passed ? CoverageVerdict.NoPass
                                      : !ecosystem.HasAssertion(content) ? CoverageVerdict.NoAssertion
                                      : ecosystem.SkipsOrExpectsFailure(content) ? CoverageVerdict.Skips
                                      : null;
            var gained = 0;
            if (rejected is null)
            {
                var own = await ecosystem.MeasureAsync(testPath, work, ct);
                if (own.Report is not { } report) rejected = CoverageVerdict.NotMeasured;
                else
                {
                    var newLines = covered.ToDictionary(c => c.Key,
                        c => (report.Files.TryGetValue(c.Key, out var lines) ? lines.Where(l => l.Value > 0).Select(l => l.Key) : [])
                             .Where(l => !c.Value.Contains(l)).ToList());
                    gained = newLines.Sum(n => n.Value.Count);
                    if (gained == 0) rejected = CoverageVerdict.NothingNew;
                    else foreach (var (file, lines) in newLines) covered[file].UnionWith(lines);
                }
            }
            return rejected is { } why ? await SetAsideAsync(Outcome(why)) : Outcome(CoverageVerdict.Kept, gained);
        }
        // Stopped while this file was being judged: nobody checked it, so it leaves like a file set aside.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await history.UndoFileAsync(run, testPath, CancellationToken.None);
            throw;
        }
        // ⚠ Anything else that fails while the file is judged (a tool, the disk, a pattern's time limit): this target is
        // set aside with its cause, and the next ones still run — the tests kept before it are still reported.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("TestCoverage.Judge", ex);
            return await SetAsideAsync(Outcome(CoverageVerdict.Failed, cause: Diagnostics.RootMessage(ex)));
        }

        // A file set aside leaves the workspace through the run's own undo, its copy kept; one that cannot leave (held by
        // another program, changed meanwhile) is said to be still there.
        async Task<CoverageTargetOutcome> SetAsideAsync(CoverageTargetOutcome outcome)
        {
            await history.UndoFileAsync(run, testPath, CancellationToken.None);
            return File.Exists(testPath) ? outcome with { StillThere = true } : outcome;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a test file that does not exist yet, in the workspace.</summary>
    internal static bool IsNewTestFile(string path, string root) =>
        !File.Exists(path) && !Directory.Exists(path)
        && Tools.PathSanitizer.IsUnderRoot(path, root)
        && CodeActions.TestFilePathResolver.IsTestFileName(Path.GetFileNameWithoutExtension(path));

    private static async Task<(bool Passed, string Output)> PassesThreeTimesAsync(ICoverageEcosystem ecosystem, string testPath,
                                                                                 CancellationToken ct)
    {
        // Three times: a test that passes once and fails the next is not a test anyone can keep.
        for (var i = 0; i < 3; i++)
        {
            var (passed, output, _) = await ecosystem.RunAsync(testPath, ct);
            if (!passed) return (false, output);
        }
        return (true, string.Empty);
    }

    /// <summary>The model's reply, or why there is none to write: no answer in <see cref="ModelDeadline"/>, only its
    /// reasoning, a reply cut at the length limit (never written: a cut file is not the file it meant), a failed request
    /// with its cause.</summary>
    private static async Task<(string? Reply, CoverageVerdict Failure, string? Cause)> AskAsync(
        IInferenceProvider client, string model, List<ChatMessageDto> messages, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelDeadline);
        try
        {
            // A short reasoning: at the default effort a reasoning model can spend the whole deadline deliberating over a
            // target without writing a character of the file. A chat turn's waits (Normal): a whole file and an example
            // test are a long prompt to read, and the quick wait would cut it before ModelDeadline does.
            var result = await client.SendChatAsync(model, [.. messages], EmptyToolRegistry.Instance, onToken: null,
                                                    ReasoningEffort.Low, deadline.Token, TaskComplexity.Normal);
            return result.CutAtLimit || result.StoppedRepeating ? (null, CoverageVerdict.ModelCut, null)
                 : result.AnswerIsReasoning                     ? (null, CoverageVerdict.NoFile, null)
                 :                                                (result.TextContent, CoverageVerdict.Kept, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, CoverageVerdict.ModelSilent, null); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("TestCoverage.Ask", ex);
            return (null, CoverageVerdict.ModelFailed, Diagnostics.RootMessage(ex));
        }
    }

    /// <summary>Writes the file through <c>write_file</c> — the approval card, the run, the confinement; <c>null</c> when
    /// it is now on disk as written, else why not: the person declined, or the tool's own answer (a permission rule, an
    /// encoding, the confinement).</summary>
    private static async Task<(CoverageVerdict Verdict, string? Cause)?> WriteAsync(IToolRegistry tools, string path, string content,
                                                                                    CancellationToken ct)
    {
        var answer = await tools.ExecuteAsync("write_file", JsonSerializer.SerializeToElement(new { path, content }), ct);
        try
        {
            if (File.Exists(path) && Normal(await File.ReadAllTextAsync(path, ct)) == Normal(content)) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Swallow("TestCoverage.ReadBack", ex); }
        return answer.Contains(Strings.WriteCancelled, StringComparison.Ordinal)
            ? (CoverageVerdict.Declined, null)
            : (CoverageVerdict.NotWritten, Tail(answer.Split('\n')[0], 300));

        static string Normal(string text) => text.Replace("\r\n", "\n").Trim();
    }

    private static readonly Regex Fence = new(@"```[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant,
                                              RegexBudget.Default);

    /// <summary>The file in a reply: its longest code block, or the reply itself.</summary>
    internal static string CodeOf(string reply)
    {
        var text   = Presentation.MarkdownParser.StripThinkTags(reply);
        var blocks = Fence.Matches(text).Select(m => m.Groups[1].Value).ToList();
        return (blocks.Count > 0 ? blocks.OrderByDescending(b => b.Length).First().TrimEnd() : text.Trim()) + "\n";
    }

    /// <summary>The user message: the target numbered with its uncovered lines, its file, an example test, the framework,
    /// the new file's path.</summary>
    internal static string Prompt(ICoverageEcosystem ecosystem, string root, CoverageTarget target, string testPath, string relative)
    {
        var lines   = ReadLines(root, target.File);
        var hint    = ecosystem.Hint(testPath);
        var example = Example(root, target.File, testPath);
        var sb = new StringBuilder();
        sb.Append("Test framework: ").AppendLine(ecosystem.FrameworkOf(testPath));
        sb.Append("New test file to write: ").AppendLine(relative);
        if (hint.Length > 0) sb.AppendLine(hint);
        sb.Append("Code under test: ").AppendLine(target.File).AppendLine();
        sb.Append($"TARGET: {target.Name} (lines {target.First}-{target.Last}); NOT COVERED lines: ")
          .AppendLine(string.Join(", ", target.Uncovered)).AppendLine();
        sb.AppendLine(Numbered(lines, target.First, target.Last, target.Uncovered.ToHashSet())).AppendLine();
        sb.AppendLine($"The whole file {target.File}:").AppendLine("```").AppendLine(Bounded(lines, target.First, target.Last))
          .AppendLine("```").AppendLine();
        if (example is { } ex)
            sb.AppendLine($"Example test file ({ex.Path}), for imports and style:").AppendLine("```").AppendLine(ex.Text).AppendLine("```");
        else
            sb.AppendLine("Example test file: (none found)");
        return sb.ToString();
    }

    /// <summary>The target, numbered, its uncovered lines marked; at most <see cref="TargetLines"/> of them, the rest said —
    /// a method of thousands of lines would otherwise fill the model's window, and its request fail.</summary>
    internal static string Numbered(string[] lines, int first, int last, HashSet<int> uncovered)
    {
        var width = last.ToString(CultureInfo.InvariantCulture).Length;
        var end   = Math.Min(Math.Min(last, lines.Length), first + TargetLines - 1);
        var sb = new StringBuilder();
        for (var n = first; n <= end; n++)
        {
            // In characters too: a minified line is a whole file on one line.
            if (sb.Length > SourceBudget) { end = n - 1; break; }
            var line = lines[n - 1].Length > 1_000 ? lines[n - 1][..1_000] + " # ... (line cut)" : lines[n - 1];
            sb.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(width))
              .Append(uncovered.Contains(n) ? " NOT COVERED" : "            ").Append(" | ").AppendLine(line);
        }
        if (end < Math.Min(last, lines.Length))
            sb.Append($"# ... (lines {end + 1}-{last} of the target not shown; aim at the NOT COVERED lines shown)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The whole file when it fits, else its head and the target's surroundings, the cuts said — within the
    /// same budget: a method of thousands of lines or a minified file would otherwise fill the model's window, and every
    /// target after it would be dropped with the failed request.</summary>
    internal static string Bounded(string[] lines, int first, int last)
    {
        var text = string.Join('\n', lines);
        if (text.Length <= SourceBudget) return text;
        var low  = Math.Max(40, first - 80);
        var high = Math.Min(lines.Length, last + 80);
        var kept = string.Join('\n', lines.Take(40).Select(Short)) + $"\n# ... (lines 41-{low} not shown)\n"
                 + string.Join('\n', lines.Skip(low).Take(high - low).Select(Short));
        return (kept.Length <= SourceBudget ? kept : kept[..SourceBudget]) + "\n# ... (rest not shown)";

        static string Short(string line) => line.Length > 1_000 ? line[..1_000] + " # ... (line cut)" : line;
    }

    /// <summary>A test file to imitate: one that imports a file of the target's own folder first (it shows how to reach
    /// the source, not a built copy), then one that names the target's file; bounded.</summary>
    private static (string Path, string Text)? Example(string root, string targetFile, string testPath)
    {
        var ext    = Path.GetExtension(testPath);
        var stem   = Path.GetFileNameWithoutExtension(targetFile);
        var folder = targetFile.Contains('/') ? targetFile[..targetFile.LastIndexOf('/')] : string.Empty;
        var last   = folder.Contains('/') ? folder[(folder.LastIndexOf('/') + 1)..] : folder;
        (string Path, int Score)? best = null;
        // The test files first, then the cap: capped first, a repository's first files are its code, and no example is found.
        // Sorted before the cap, so the same example is chosen on every machine.
        var candidates = WorkspaceScan.ShallowestFirst(WorkspaceScan.EnumerateFiles(root, "*" + ext)
            .Where(f => CodeActions.TestFilePathResolver.IsTestFileName(Path.GetFileNameWithoutExtension(f))
                        && !Path.GetFileNameWithoutExtension(f).Contains("coverage", StringComparison.OrdinalIgnoreCase)))
            .Take(ExamplesScanned);
        foreach (var file in candidates)
        {
            if (Governance.ContentExclusions.NoticeFor(file, null) is not null) continue;
            var text  = ReadOrEmpty(file);
            var score = CountOf(text, stem) + (Path.GetFileName(file).Contains(stem, StringComparison.OrdinalIgnoreCase) ? 5 : 0)
                      + (last.Length > 0 && text.Contains(last + "/", StringComparison.Ordinal) ? 50 : 0);
            if (best is null || score > best.Value.Score) best = (file, score);
        }
        if (best is not { } chosen) return null;
        var content = ReadOrEmpty(chosen.Path);
        return (Path.GetRelativePath(root, chosen.Path).Replace('\\', '/'),
                content.Length <= ExampleBudget ? content : content[..ExampleBudget] + "\n# ... (rest not shown)");
    }

    private static int CountOf(string text, string word)
    {
        if (word.Length == 0) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0) { count++; at += word.Length; }
        return count;
    }

    private static string[] ReadLines(string root, string relative) => ReadOrEmpty(Path.Combine(root, relative)).Split('\n')
        .Select(l => l.TrimEnd('\r')).ToArray();

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? Tools.TextFileEncoding.ReadText(path) : string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();

    /// <summary>The head and the tail of a tool's output: a runner that cannot start says why first (vitest's "Startup
    /// Error"), then prints a stack; a suite's verdict is at its end.</summary>
    internal static string Ends(string text, int head, int tail) =>
        text.Length <= head + tail ? text.Trim() : text[..head].TrimEnd() + "\n…\n" + text[^tail..].TrimStart();

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with forward slashes; empty for the root.</summary>
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/') is var relative && relative == "." ? string.Empty : relative;

    private static string Percent(int covered, int total) =>
        (total == 0 ? 0 : 100.0 * covered / total).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>The report: the coverage before → after, the files that gained, the tests kept and set aside with their
    /// reason, what is left for another run.</summary>
    internal static string Report(string header, CoberturaReport before, CoverageRun? after, IReadOnlyList<string> files,
                                  IReadOnlyList<CoverageTargetOutcome> outcomes, int left, int excluded, bool suiteGreen,
                                  bool brokenAfter = false)
    {
        var sb = new StringBuilder(header).AppendLine().AppendLine();
        var (c0, t0) = before.Of(files);
        if (after?.Report is { } measured)
        {
            var (c1, t1) = measured.Of(files);
            var delta = (t1 == 0 ? 0 : 100.0 * c1 / t1) - (t0 == 0 ? 0 : 100.0 * c0 / t0);
            sb.AppendLine(Strings.CoverageSummary(Percent(c0, t0), Percent(c1, t1),
                                                  delta.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture), c1, t1));
            var gained = files.Where(f => measured.Of([f]).Covered > before.Of([f]).Covered).ToList();
            if (gained.Count > 0)
            {
                sb.AppendLine().AppendLine(Strings.CoverageFilesHeader);
                foreach (var f in gained)
                    sb.AppendLine(Strings.CoverageFileRow(f, Percent(before.Of([f]).Covered, before.Of([f]).Total),
                                                          Percent(measured.Of([f]).Covered, measured.Of([f]).Total)));
            }
        }
        else if (after is not null)   // null: stopped before the measure
        {
            sb.AppendLine(Strings.CoverageAfterFailed);
        }

        var kept = outcomes.Where(o => o.Verdict == CoverageVerdict.Kept).ToList();
        if (kept.Count > 0)
        {
            sb.AppendLine().AppendLine(Strings.CoverageKeptHeader(kept.Count));
            foreach (var o in kept) sb.AppendLine(Strings.CoverageKeptRow(o.TestFile, o.Target.Name, o.NewLines));
        }
        var aside = outcomes.Where(o => o.Verdict != CoverageVerdict.Kept).ToList();
        if (aside.Count > 0)
        {
            sb.AppendLine().AppendLine(Strings.CoverageSetAsideHeader(aside.Count));
            foreach (var o in aside) sb.AppendLine(Strings.CoverageSetAsideRow(o.Target.Name, Why(o)) + (o.StillThere ? " " + Strings.CoverageStillThere : string.Empty));
        }
        if (left > 0) sb.AppendLine().AppendLine(Strings.CoverageLeft(left));
        if (excluded > 0) sb.AppendLine(Strings.CoverageExcluded(excluded));
        if (!suiteGreen) sb.AppendLine().AppendLine(Strings.CoverageSuiteRed);
        if (brokenAfter) sb.AppendLine().AppendLine(Strings.CoverageSuiteBrokenAfter);
        return sb.ToString().TrimEnd();
    }

    private static string Why(CoverageTargetOutcome outcome) => outcome.Verdict switch
    {
        CoverageVerdict.NoPass       => Strings.CoverageWhyNoPass,
        CoverageVerdict.NoAssertion  => Strings.CoverageWhyNoAssertion,
        CoverageVerdict.Skips        => Strings.CoverageWhySkips,
        CoverageVerdict.NothingNew   => Strings.CoverageWhyNothingNew,
        CoverageVerdict.NotMeasured  => Strings.CoverageWhyNotMeasured,
        CoverageVerdict.ModelSilent  => Strings.CoverageWhyModelSilent,
        CoverageVerdict.ModelCut     => Strings.CoverageWhyModelCut,
        CoverageVerdict.ModelFailed  => Strings.CoverageWhyModelFailed(outcome.Cause ?? "?"),
        CoverageVerdict.NoFile       => Strings.CoverageWhyNoFile,
        CoverageVerdict.Declined     => Strings.CoverageWhyDeclined,
        CoverageVerdict.NotWritten   => Strings.CoverageWhyNotWritten(outcome.Cause ?? "?"),
        CoverageVerdict.Failed       => Strings.CoverageWhyFailed(outcome.Cause ?? "?"),
        _                            => Strings.CoverageWhyNotATestPath,
    };
}
