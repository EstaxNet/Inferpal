using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Inference;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/check</c> — review the current git diff against <c>.inferpal/checks/*.md</c>
/// and give back findings <b>anchored to the diff</b>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the VS view-model, which owned the whole flow and was therefore the only
/// front-end able to run it: <c>/check</c> answered "unavailable" over RPC. Per CLAUDE.md, a
/// command served by both front-ends lives here — copying it into <c>HostSlashCommands</c>
/// instead is the drift this repository has already paid for twice.
/// </para>
/// <para>
/// Git is injected rather than shelled out to from here: it keeps the handler testable without a
/// repository, and each front-end already owns a runner.
/// </para>
/// </remarks>
internal static class CheckCommandHandler
{
    /// <param name="Message">Markdown to display; null when the caller must scaffold instead.</param>
    /// <param name="Scaffold">Set for <c>/check init</c> — the example file to create.</param>
    internal readonly record struct CheckCommandResult(
        string? Message,
        RulesChecksPromptsCommandHandler.ScaffoldRequest? Scaffold = null);

    /// <param name="client">Inference provider; the review uses the chat-role model.</param>
    /// <param name="config">Read for model resolution only.</param>
    /// <param name="projectRoot">Repository root — where checks are loaded and git runs.</param>
    /// <param name="parts">Tokenised command: <c>init</c>, a check name, or nothing.</param>
    /// <param name="git">Git runner supplied by the front-end.</param>
    /// <param name="onProgress">Status line while the model reviews; null = silent.</param>
    public static async Task<CheckCommandResult> HandleAsync(
        IInferenceProvider client, InferpalConfig config, string projectRoot, string[] parts,
        GitRunner git, Action<string>? onProgress, CancellationToken ct)
    {
        var arg = parts.Length >= 2 ? string.Join(" ", parts[1..]).Trim() : null;

        if (string.Equals(arg, "init", StringComparison.OrdinalIgnoreCase))
            return new(null, RulesChecksPromptsCommandHandler.Checks(projectRoot, parts).Scaffold);

        // Checks that cannot be read are named on every answer that depends on them: dropped silently, the
        // diff would be reviewed against fewer checks than the user wrote.
        var checks = ChecksService.Load(Path.Combine(projectRoot, ".inferpal", "checks"), out var unreadable);
        string Named(string message) =>
            RulesChecksPromptsCommandHandler.Unreadable(unreadable) is { } note ? message + "\n\n" + note : message;

        if (checks.Count == 0) return new(Named(Strings.ChecksNone));

        if (!string.IsNullOrEmpty(arg))
        {
            var one = checks.FirstOrDefault(c => c.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (one is null) return new(Named(Strings.CheckUnknownName(arg)));
            checks = [one];
        }

        // Staged first, unstaged as a fallback — same rule as /commit, so the two commands always
        // talk about the same change.
        // ⚠ Same gate as /commit, for the same reason: `Strings.CheckNoDiff` says "the working tree
        // is clean", which is a claim about the user's repository — and a git that refused would
        // instead have its `fatal:` line reviewed against the user's checks, since RunAsync appends
        // stderr to the output.
        var stagedRun = await git("diff --staged", ct);
        if (GitProcess.FailureNote("diff --staged", stagedRun) is { } stagedFailed)
            return new(Named(stagedFailed));
        var staged = stagedRun.Output;
        string diff;
        List<string> notReviewed = [];
        if (string.IsNullOrWhiteSpace(staged))
        {
            var unstagedRun = await git("diff", ct);
            if (GitProcess.FailureNote("diff", unstagedRun) is { } unstagedFailed)
                return new(Named(unstagedFailed));
            var statusRun = await git("status --short", ct);
            if (GitProcess.FailureNote("status --short", statusRun) is { } statusFailed)
                return new(Named(statusFailed));
            var unstaged = unstagedRun.Output;
            var status   = statusRun.Output;
            if (string.IsNullOrWhiteSpace(unstaged) && string.IsNullOrWhiteSpace(status))
                return new(Strings.CheckNoDiff);
            diff = GitCommitPolicy.BuildUnstagedContext(status, unstaged);

            // ⚠ A NEW file is part of the change under review, and `git diff` never shows one: reviewed from the
            // status line alone, "?? src/NewService.cs" came back "the checks turned up nothing on this diff" — a clean
            // verdict on code nobody read. (/commit leaves them out on purpose: /commit-exec commits tracked files only.)
            var (_, untracked) = GitCommitPolicy.SplitUntracked(status);
            if (untracked.Count > 0)
            {
                var (newFiles, notRead) = await NewFilesAsync(projectRoot, git, untracked, diff.Length, ct);
                diff       += newFiles;
                notReviewed = notRead;
            }
        }
        else
        {
            diff = GitCommitPolicy.BuildStagedContext(staged);
        }

        // ⚠ The cut rides ABOVE the findings, because it qualifies them: "the checks turned up
        // nothing on this diff" is a verdict the user acts on, and on a capped diff it is a verdict
        // about the part that fit. Most real changes exceed the cap — the model is told, and the
        // person reading the answer was not.
        var capped = GitCommitPolicy.CapDiff(diff);

        onProgress?.Invoke(Strings.CheckReviewingLabel);

        var history = new List<ChatMessageDto>
        {
            new("system", Strings.CheckReviewSystemPrompt),
            new("user",   ChecksService.BuildReviewPrompt(checks, capped.Text)),
        };

        string answer;
        bool   cut, reasoningOnly;
        var    model = ModelRouter.Resolve(config, ModelRole.Chat);
        try
        {
            var result = await client.SendChatAsync(model, history, EmptyToolRegistry.Instance, null, ct);
            // A finding drafted while the model reasons is not one it gave — inline reasoning comes off first.
            answer        = MarkdownParser.WithoutLeadingReasoning(result.TextContent);
            cut           = result.CutAtLimit;
            reasoningOnly = result.AnswerIsReasoning;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Strings.MsgError(ex.Message)); }

        // ⚠ An empty reply, or one that is only the model's reasoning, is not a review. Rendered, it read "no anchored
        // finding — the checks turned up nothing on this diff": a clean verdict nobody gave.
        if (reasoningOnly) return new(Named(Strings.CheckReviewOnlyReasoning(model)));
        if (string.IsNullOrWhiteSpace(answer)) return new(Named(Strings.MsgEmptyResponseFrom(model, client.ServerAddress)));

        // Anchors come from the very text the model was shown, so a location is checked against
        // what the model could actually see — not against the working tree, which may have moved.
        var rendered = CheckReviewParser.Render(CheckReviewParser.Parse(answer, DiffAnchors.Parse(capped.Text)));
        // ⚠ Same reason, other end: a review that stopped at the length limit lists the findings it
        // reached, and the absence of the rest reads as "the rest is fine" — a verdict nobody gave.
        if (cut)
            rendered = Strings.CheckReviewCut + "\n\n" + rendered;
        if (capped.IsTruncated)
            rendered = Strings.CheckDiffTruncated(capped.Kept, capped.Total) + "\n\n" + rendered;
        if (notReviewed.Count > 0)
            rendered = Strings.CheckNewFilesNotReviewed(notReviewed.Count, GitCommitPolicy.NameList(notReviewed))
                       + "\n\n" + rendered;
        return new(Named(rendered));
    }

    /// <summary>
    /// The files git does not track yet, as the diffs they will be once added, while the review has room — and the
    /// ones it does not read (past the size limit, binary, unreadable), NAMED: dropped, they read as reviewed.
    /// </summary>
    /// <param name="untracked">The status's own list, named whole when git cannot list the files one by one.</param>
    /// <param name="used">Characters of the review already taken by the status and the diff.</param>
    /// <summary>The listing of the repository's new files, one path per file, from its top.</summary>
    internal const string NewFilesListing = "ls-files --others --exclude-standard --full-name -- :/";

    private static async Task<(string Diff, List<string> NotRead)> NewFilesAsync(
        string projectRoot, GitRunner git, IReadOnlyList<string> untracked, int used, CancellationToken ct)
    {
        var notRead = new List<string>();
        // One path per file: the status collapses a new folder into "?? folder/".
        // ⚠ The WHOLE repository (":/"), paths from its top (--full-name): `git status` lists every new file of the
        // repository, `ls-files` only the folder it runs in — a solution in src/ or a monorepo opened on one package
        // left a new file beside it ("?? ../deploy.yml") neither read nor named, under "the checks turned up nothing".
        var (listing, exit) = await git(NewFilesListing, ct);
        if (exit != 0)
        {
            notRead.AddRange(untracked);
            return ("", notRead);
        }

        var sb = new System.Text.StringBuilder();
        var files = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.TrimEnd('\r'))
                           .Where(l => l.Length > 0)
                           .OrderBy(l => l, StringComparer.Ordinal);
        var top = GitProcess.WorkTreeOf(projectRoot) ?? projectRoot;
        foreach (var relative in files)
        {
            if (used + sb.Length >= GitCommitPolicy.MaxDiffChars) { notRead.Add(relative); continue; }
            var full = Path.Combine(top, relative);
            try
            {
                if (!File.Exists(full)) continue;   // deleted since the listing: nothing to review
                if (Tools.TextFileEncoding.IsBinaryFile(full)) { notRead.Add(relative); continue; }
                sb.Append("\n\n").Append(GitCommitPolicy.NewFileDiff(relative, Tools.TextFileEncoding.ReadText(full)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notRead.Add(relative);
            }
        }
        return (sb.Length == 0 ? "" : "\n\ngit diff (new files, not yet added to git):" + sb, notRead);
    }
}
