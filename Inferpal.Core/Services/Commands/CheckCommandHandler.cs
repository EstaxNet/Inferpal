using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Inference;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/check</c> — review the current git diff, or a branch, a range, a commit (<see cref="CheckTarget"/>),
/// against <c>.inferpal/checks/*.md</c> and give back findings <b>anchored to the diff</b>.
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
    /// <param name="parts">Tokenised command: <c>init</c>, a check name, a git revision or range, or nothing.</param>
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
        // A check's name that is also a revision, a revision that is also a path: every answer says how to review the other.
        string? alsoRevision = null;
        string? alsoPath = null;
        string Named(string message)
        {
            if (RulesChecksPromptsCommandHandler.Unreadable(unreadable) is { } note) message += "\n\n" + note;
            if (alsoRevision is not null) message += "\n\n" + alsoRevision;
            return alsoPath is null ? message : message + "\n\n" + alsoPath;
        }

        // ⚠ No check is not a reason to refuse: the review is the general one the system prompt asks for, the checks
        // being added criteria — and the answer says it was made without any.
        // ⚠ A check's name wins over a revision of the same name: /check <name> keeps its meaning, and the answer says how
        // to review the revision. Anything else is read as a revision; neither is refused naming both.
        var narrowed = false;
        CheckTarget? target = null;
        string? scope = null;
        if (arg is { Length: > 0 })
        {
            var one = checks.FirstOrDefault(c => c.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (one is not null)
            {
                checks   = [one];
                narrowed = true;
                if (await CheckTarget.RevisionSpellingAsync(arg, git, ct) is { } spelling)
                    alsoRevision = Strings.CheckNameAlsoRevision(arg, spelling);
            }
            else if (unreadable.Any(f => Path.GetFileNameWithoutExtension(f).Equals(arg, StringComparison.OrdinalIgnoreCase)))
            {
                // The check of that name exists and could not be read: the note says so, and no other remedy is true
                // (`/checks` would not list it, `/checks init` would write another).
                return new(Named(string.Empty).TrimStart());
            }
            else
            {
                var (resolved, nothing) = await CheckTarget.ResolveAsync(arg, git, ct);
                var path = PathScope(arg, projectRoot);
                // ⚠ A revision wins over a path of the same name (a branch "docs", a folder "cafe" that is a hash prefix):
                // the answer names the spelling that reviews the path, never only the other meaning.
                if ((resolved is not null || nothing is not null) && path is not null)
                    alsoPath = Strings.CheckRevisionAlsoPath(arg.Trim('"'));
                if (nothing is not null) return new(Named(nothing));
                if (resolved is not null) target = resolved;
                else if (path is not null)
                {
                    // A file or a folder: the change under it — what the Explorer's "Review Changes" sends. Bounded by the
                    // repository, as the whole-change review is, not by the workspace.
                    if (!Tools.PathSanitizer.IsUnderRoot(path, GitProcess.WorkTreeOf(projectRoot) ?? projectRoot))
                        return new(Named(Strings.CheckPathOutside(arg.Trim('"'))));
                    scope = path;
                }
                else
                    return new(Named(checks.Count == 0 ? Strings.CheckUnknownRevision(arg)
                                                       : Strings.CheckUnknownName(arg, CheckNames(checks))));
            }
        }

        // ⚠ The lines of a file the workspace's ignore files exclude are its content: its sections leave the diff, and the
        // review says it did not read them — never a clean verdict on what it never saw.
        var excluded = new Governance.ContentExclusions.Tally();
        var workTree = GitProcess.WorkTreeOf(projectRoot);
        string diff;
        List<string> notReviewed = [];
        var stagedOnly = false;
        if (target is not null)
        {
            var run = await git(target.DiffArgs, ct);
            if (GitProcess.FailureNote(target.DiffArgs, run) is { } failed) return new(Named(failed));
            var text = Governance.ContentExclusions.WithoutExcludedFiles(run.Output, workTree, excluded);
            if (string.IsNullOrWhiteSpace(text))
                return new(Named(excluded.Count > 0 ? excluded.ForPerson() : Strings.CheckTargetDiffEmpty(target.Shown)));
            diff = $"git diff {target.Shown}:\n{text}";
        }
        else
        {
            // Staged first, unstaged as a fallback — same rule as /commit, so the two commands always
            // talk about the same change.
            // ⚠ Same gate as /commit, for the same reason: `Strings.CheckNoDiff` says "the working tree
            // is clean", which is a claim about the user's repository — and a git that refused would
            // instead have its `fatal:` line reviewed against the user's checks, since RunAsync appends
            // stderr to the output.
            // A path scopes every command to it, new files included.
            var spec       = scope is null ? string.Empty : " -- " + Pathspec(scope, projectRoot);
            var stagedArgs = "diff --staged" + spec;
            var stagedRun  = await git(stagedArgs, ct);
            if (GitProcess.FailureNote(stagedArgs, stagedRun) is { } stagedFailed)
                return new(Named(stagedFailed));
            // Staged or not is decided on what git staged, excluded files included: the change under review stays the one
            // /commit describes.
            var staged = Governance.ContentExclusions.WithoutExcludedFiles(stagedRun.Output, workTree, excluded);
            if (string.IsNullOrWhiteSpace(stagedRun.Output))
            {
                var unstagedArgs = "diff" + spec;
                var unstagedRun  = await git(unstagedArgs, ct);
                if (GitProcess.FailureNote(unstagedArgs, unstagedRun) is { } unstagedFailed)
                    return new(Named(unstagedFailed));
                var statusArgs = "status --short" + spec;
                var statusRun  = await git(statusArgs, ct);
                if (GitProcess.FailureNote(statusArgs, statusRun) is { } statusFailed)
                    return new(Named(statusFailed));
                var unstaged = Governance.ContentExclusions.WithoutExcludedFiles(unstagedRun.Output, workTree, excluded);
                var status   = statusRun.Output;
                if (string.IsNullOrWhiteSpace(unstaged) && string.IsNullOrWhiteSpace(status))
                    return new(Named(excluded.Count > 0 ? excluded.ForPerson()
                                   : scope is not null ? Strings.CheckPathNoDiff(ShownScope(scope, projectRoot))
                                   : Strings.CheckNoDiff));
                diff = GitCommitPolicy.BuildUnstagedContext(status, unstaged);

                // ⚠ A NEW file is part of the change under review, and `git diff` never shows one: reviewed from the
                // status line alone, "?? src/NewService.cs" came back "the review turned up nothing on this diff" — a clean
                // verdict on code nobody read. (/commit leaves them out on purpose: /commit-exec commits tracked files only.)
                var (_, untracked) = GitCommitPolicy.SplitUntracked(status);
                if (untracked.Count > 0)
                {
                    var (newFiles, notRead) = await NewFilesAsync(projectRoot, git, untracked, diff.Length, excluded, ct,
                                                                  scope is null ? null : Pathspec(scope, projectRoot));
                    diff       += Governance.ContentExclusions.WithoutExcludedFiles(newFiles, workTree, excluded);
                    notReviewed = notRead;
                }
            }
            else if (string.IsNullOrWhiteSpace(staged))
            {
                // Only excluded files are staged: nothing the review may read.
                return new(Named(excluded.ForPerson()));
            }
            else
            {
                diff = GitCommitPolicy.BuildStagedContext(staged);
                stagedOnly = true;
            }
        }
        if (excluded.Count > 0) diff = excluded.Note() + "\n\n" + diff;

        // ⚠ The cut rides ABOVE the findings, because it qualifies them: "the review turned up
        // nothing on this diff" is a verdict the user acts on, and on a capped diff it is a verdict
        // about the part that fit. Most real changes exceed the cap — the model is told, and the
        // person reading the answer was not.
        var capped = GitCommitPolicy.CapDiff(diff);

        onProgress?.Invoke(Strings.CheckReviewingLabel);

        // ⚠ One English text whatever the interface language (the model's instructions are never translated), the
        // interface language riding on the reply-language line, the severity words pinned to English in both texts.
        var history = new List<ChatMessageDto>
        {
            new("system", ReviewSystemPrompt(narrowed)),
            new("user",   ChecksService.BuildReviewPrompt(checks, capped.Text)),
        };

        string answer;
        bool   cut, reasoningOnly;
        var    model = ModelRouter.Resolve(config, ModelRole.Chat);
        try
        {
            var result = await client.SendChatAsync(model, history, EmptyToolRegistry.Instance, null,
                                                    ReasoningEffortSetting.Chat(config), ct);
            // A finding drafted while the model reasons is not one it gave — inline reasoning comes off first.
            answer        = MarkdownParser.WithoutLeadingReasoning(result.TextContent);
            cut           = result.CutAtLimit;
            reasoningOnly = result.AnswerIsReasoning;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Strings.MsgError(ex.Message)); }

        // ⚠ An empty reply, or one that is only the model's reasoning, is not a review. Rendered, it read "no anchored
        // finding — the review turned up nothing on this diff": a clean verdict nobody gave.
        if (reasoningOnly) return new(Named(Strings.CheckReviewOnlyReasoning(model)));
        if (string.IsNullOrWhiteSpace(answer)) return new(Named(Strings.MsgEmptyResponseFrom(model, client.ServerAddress)));

        // Anchors come from the very text the model was shown, so a location is checked against
        // what the model could actually see — not against the working tree, which may have moved.
        var rendered = CheckReviewParser.Render(CheckReviewParser.Parse(answer, DiffAnchors.Parse(capped.Text)), narrowed);
        // ⚠ Same reason, other end: a review that stopped at the length limit lists the findings it
        // reached, and the absence of the rest reads as "the rest is fine" — a verdict nobody gave.
        if (cut)
            rendered = Strings.CheckReviewCut + "\n\n" + rendered;
        // ⚠ The remedy follows what was reviewed: a committed change has nothing to stage in smaller pieces.
        if (capped.IsTruncated)
            rendered = (target is null ? Strings.CheckDiffTruncated(capped.Kept, capped.Total)
                      : target.IsCommit ? Strings.CheckCommitDiffTruncated(capped.Kept, capped.Total)
                      : Strings.CheckRangeDiffTruncated(capped.Kept, capped.Total))
                       + "\n\n" + rendered;
        if (notReviewed.Count > 0)
            rendered = Strings.CheckNewFilesNotReviewed(notReviewed.Count, GitCommitPolicy.NameList(notReviewed))
                       + "\n\n" + rendered;
        if (excluded.Count > 0)
            rendered = excluded.ForPerson() + "\n\n" + rendered;
        // What was reviewed rides on top: a verdict on a commit read as one on the working tree is the wrong verdict.
        // Above the findings too: a verdict read as one against the person's checks is the wrong verdict.
        if (checks.Count == 0) rendered = Strings.CheckWithoutChecks + "\n\n" + rendered;
        if (target is not null) rendered = target.Label + "\n\n" + rendered;
        // A verdict on a folder read as one on the whole change is the wrong verdict — and on its staged part, as one on all
        // its changes.
        if (scope is not null)
            rendered = (stagedOnly ? Strings.CheckPathStagedLabel(ShownScope(scope, projectRoot))
                                   : Strings.CheckPathLabel(ShownScope(scope, projectRoot))) + "\n\n" + rendered;
        return new(Named(rendered));
    }

    /// <summary>
    /// <paramref name="arg"/> as a file or a folder that exists — absolute, or relative to <paramref name="projectRoot"/>;
    /// quotes around it dropped — or <c>null</c>.
    /// </summary>
    internal static string? PathScope(string arg, string projectRoot)
    {
        var text = arg.Trim().Trim('"');
        if (text.Length == 0) return null;
        try
        {
            // Fully qualified, never merely rooted: "C:src" or "\src" would resolve against the process's directory.
            var full = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.IsPathFullyQualified(text) ? text : Path.Combine(projectRoot, text)));
            return File.Exists(full) || Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>The git pathspec of <paramref name="scope"/>: relative to the folder git runs in, forward slashes, quoted.</summary>
    /// <remarks>
    /// ⚠ Never the absolute path in a quoted command-line argument: a folder ending in a separator writes <c>\"</c>, which
    /// escapes the closing quote — git then matched nothing, exited 0, and the answer was "no uncommitted change" on a
    /// folder that had changes. ⚠ And case-insensitive where the file system is (<c>:(icase)</c>): git's pathspecs are
    /// not, so a folder typed or listed in another case than the disk's also matched nothing.
    /// </remarks>
    internal static string Pathspec(string scope, string projectRoot)
    {
        var relative = Path.GetRelativePath(projectRoot, scope).Replace('\\', '/');
        var magic    = PathComparer.Comparison == StringComparison.OrdinalIgnoreCase ? ":(icase)" : string.Empty;
        return $"\"{magic}{relative}\"";
    }

    /// <summary>The scope as the answer names it: relative to the root, with forward slashes.</summary>
    private static string ShownScope(string scope, string projectRoot) =>
        Governance.ContentExclusions.Shown(scope, projectRoot);

    /// <summary>The checks a refusal names, so a mistyped name is corrected without another command.</summary>
    private static string CheckNames(IReadOnlyList<ReviewCheck> checks) =>
        string.Join(", ", checks.Select(c => $"`{c.Name}`"));

    /// <summary>
    /// The system prompt of a review: the whole change with the checks as added criteria, or — for <c>/check
    /// &lt;name&gt;</c> — that one check only; then the line naming the reply language.
    /// </summary>
    internal static string ReviewSystemPrompt(bool narrowed) =>
        (narrowed ? ModelPrompts.CheckReviewNamed : ModelPrompts.CheckReview)
        + SystemPromptBuilder.ReplyLanguage(Strings.UiCulture);

    /// <summary>The listing of the repository's new files, one path per file, from its top.</summary>
    internal const string NewFilesListing = "ls-files --others --exclude-standard --full-name -- :/";

    /// <summary>
    /// The files git does not track yet, as the diffs they will be once added, while the review has room — and the
    /// ones it does not read (past the size limit, binary, unreadable), NAMED: dropped, they read as reviewed.
    /// </summary>
    /// <param name="untracked">The status's own list, named whole when git cannot list the files one by one.</param>
    /// <param name="used">Characters of the review already taken by the status and the diff.</param>
    /// <param name="excluded">Where the new files the workspace's ignore files exclude are counted — never read, so they
    /// take none of the review's room.</param>
    private static async Task<(string Diff, List<string> NotRead)> NewFilesAsync(
        string projectRoot, GitRunner git, IReadOnlyList<string> untracked, int used,
        Governance.ContentExclusions.Tally excluded, CancellationToken ct, string? pathspec = null)
    {
        var notRead = new List<string>();
        // One path per file: the status collapses a new folder into "?? folder/".
        // ⚠ The WHOLE repository (":/"), paths from its top (--full-name): `git status` lists every new file of the
        // repository, `ls-files` only the folder it runs in — a solution in src/ or a monorepo opened on one package
        // left a new file beside it ("?? ../deploy.yml") neither read nor named, under "the review turned up nothing".
        // A review scoped to a path lists that path's only: the repository's others are not the change under review.
        var (listing, exit) = await git(pathspec is null ? NewFilesListing : NewFilesListing[..^2] + pathspec, ct);
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
            var full = Path.Combine(top, relative);
            if (Governance.ContentExclusions.Of(full) is { } why) { excluded.Add(full, why); continue; }
            if (used + sb.Length >= GitCommitPolicy.MaxDiffChars) { notRead.Add(relative); continue; }
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
