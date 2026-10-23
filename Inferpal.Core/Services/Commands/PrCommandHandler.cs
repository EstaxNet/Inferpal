using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Commands;

/// <summary>
/// <c>/pr-description [base]</c> — a pull request's title and description for the current branch: what it brings since it left
/// <c>base</c> (by default the repository's default branch), written by the utility model from the commits' subjects and
/// the diff. Nothing is pushed or created: the proposal is shown, in a block to copy.
/// </summary>
internal static class PrCommandHandler
{
    /// <summary>The commits whose subjects go to the model; past it, the cut is said to the model.</summary>
    internal const int MaxSubjects = 100;

    private static readonly CheckTarget.RangeWords PrWords =
        new(Strings.PrDescribedRange, Strings.PrRangeEmpty, Strings.PrNoCommonBase);

    /// <returns>What the chat shows: the range described, the notices, the proposal — or why there is none.</returns>
    public static async Task<string> HandleAsync(IInferenceProvider client, InferpalConfig config, string[] parts,
        GitRunner git, Action<string>? onProgress, CancellationToken ct)
    {
        var typed = parts.Length >= 2 ? string.Join(" ", parts[1..]).Trim() : string.Empty;
        string baseName;
        if (typed.Length > 0) baseName = typed;
        else
        {
            const string head = "rev-parse --symbolic-full-name HEAD";
            var headRun = await git(head, ct);
            if (GitProcess.FailureNote(head, headRun) is { } headFailed) return headFailed;
            var branch = GitProcess.FirstLine(headRun.Output);
            var found = await CheckTarget.DefaultBaseAsync(branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : null,
                                                           git, ct);
            if (found is null) return Strings.PrNoBase;
            baseName = found;
        }
        if (!CheckTarget.IsRevisionText(baseName)) return Strings.PrUnknownBase(baseName);

        var (target, nothing) = await CheckTarget.ResolveAsync($"{baseName}...HEAD", git, ct, PrWords);
        if (target is null) return nothing ?? Strings.PrUnknownBase(baseName);

        onProgress?.Invoke(Strings.PrProposingLabel);
        // Oldest first, as the branch was written; merges add no subject of their own. ⚠ Never `--max-count` with
        // `--reverse`: git limits BEFORE it reverses, so the newest commits would come, under a note saying "the first".
        // The range is the one resolved above, by object names.
        // --no-show-signature: with log.showSignature set, the signature check would come out as subjects.
        var log = $"log --no-merges --reverse --no-show-signature --format=%s {target.Commits}";
        var logRun = await git(log, ct);
        if (GitProcess.FailureNote(log, logRun) is { } logFailed) return logFailed;
        var subjects = logRun.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        var diffRun = await git(target.DiffArgs, ct);
        if (GitProcess.FailureNote(target.DiffArgs, diffRun) is { } diffFailed) return diffFailed;
        // ⚠ The lines of a file the workspace's ignore files exclude are its content: they leave what the model reads,
        // and the answer says the description did not read them.
        var excluded = new Governance.ContentExclusions.Tally();
        var workTree = Governance.ContentExclusions.AnyRules()
            ? (await git("rev-parse --show-toplevel", ct)) is { ExitCode: 0 } top ? top.Output.Trim() : null
            : null;
        var diff = Governance.ContentExclusions.WithoutExcludedFiles(diffRun.Output, workTree, excluded);

        var notices = new List<string>();
        if (excluded.Count > 0) notices.Add(excluded.ForPerson());
        // Nothing left to read — commits that cancel out, or that touch excluded files only: a model asked anyway would
        // describe code it never saw.
        if (string.IsNullOrWhiteSpace(diff)) return Compose(target.Label, notices, Strings.PrDiffEmpty(target.Shown));
        var capped = GitCommitPolicy.CapDiff(diff);
        if (capped.IsTruncated) notices.Add(Strings.PrDiffTruncated(capped.Kept, capped.Total));
        if (subjects.Count > MaxSubjects) notices.Add(Strings.PrSubjectsCut(MaxSubjects, subjects.Count));

        var commits = string.Join("\n", subjects.Take(MaxSubjects).Select(s => "- " + s));
        if (subjects.Count > MaxSubjects) commits += $"\n(only the first {MaxSubjects} of {subjects.Count} commits are listed)";
        var context = (excluded.Count > 0 ? excluded.Note() + "\n\n" : string.Empty)
                    + $"Commits on this branch, oldest first:\n{commits}\n\nDiff since it left {baseName}:\n{capped.Text}";

        string answer;
        try
        {
            var model  = await ModelRouter.ResolveUtilityAsync(config, client, ct);
            var result = await client.RunAgentAsync(
                model:   model,
                history: BuildRequest(context),
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      ct,
                effort:  ReasoningEffort.Off);

            // No description, no "Described:" above it: a failed run returns its error as its reply, a reasoning promoted
            // to the reply is a draft, a reply stopped for repeating itself is a loop — none is a description to copy.
            if (result.Failed) return result.FinalResponse;
            if (result.AnswerIsReasoning) return Strings.MsgOnlyReasoningFrom(model);
            if (result.AnswerRepeating) return Strings.AnswerStoppedRepeating;
            // An artifact loses only the reasoning in FRONT of it: a description may name a reasoning tag in its prose.
            answer = MarkdownParser.WithoutEnclosingFence(MarkdownParser.WithoutLeadingReasoning(result.FinalResponse)).Trim();
            if (answer.Length == 0) return Strings.MsgEmptyResponseFrom(model, client.ServerAddress);
            if (result.AnswerCut) notices.Add(Strings.PrDescriptionCut);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Strings.MsgError(ex.Message); }

        // In a block to copy as it is: a description is pasted, Markdown and all.
        return Compose(target.Label, notices, MarkdownParser.Fence(answer, "markdown"));
    }

    /// <summary>The model-facing request: English, like every prompt; the description matches the commits' language.</summary>
    internal static List<ChatMessageDto> BuildRequest(string context) =>
    [
        new("system",
            "You write pull request descriptions. Reply with ONLY the pull request: its title on the first line (under 72 " +
            "characters, no prefix such as 'Title:'), a blank line, then the description in Markdown — a short summary of " +
            "what the branch changes and why, then the main changes as a list, then anything a reviewer should test or " +
            "watch. Describe only what the commits and the diff show; invent nothing. Match the language of the commit " +
            "messages."),
        new("user", $"Write the pull request for this branch.\n\n{context}"),
    ];

    private static string Compose(string label, List<string> notices, string body) =>
        string.Join("\n\n", [label, .. notices, body]);
}
