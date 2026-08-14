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
/// and give back findings <b>anchored to the diff</b> (roadmap §15).
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

        var checks = ChecksService.Load(Path.Combine(projectRoot, ".inferpal", "checks"));
        if (checks.Count == 0) return new(Strings.ChecksNone);

        if (!string.IsNullOrEmpty(arg))
        {
            var one = checks.FirstOrDefault(c => c.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (one is null) return new(Strings.CheckUnknownName(arg));
            checks = [one];
        }

        // Staged first, unstaged as a fallback — same rule as /commit, so the two commands always
        // talk about the same change.
        var staged = (await git("diff --staged", ct)).Output;
        string diff;
        if (string.IsNullOrWhiteSpace(staged))
        {
            var unstaged = (await git("diff", ct)).Output;
            var status   = (await git("status --short", ct)).Output;
            if (string.IsNullOrWhiteSpace(unstaged) && string.IsNullOrWhiteSpace(status))
                return new(Strings.CheckNoDiff);
            diff = GitCommitPolicy.BuildUnstagedContext(status, unstaged);
        }
        else
        {
            diff = GitCommitPolicy.BuildStagedContext(staged);
        }

        diff = GitCommitPolicy.CapDiff(diff);

        onProgress?.Invoke(Strings.CheckReviewingLabel);

        var history = new List<ChatMessageDto>
        {
            new("system", Strings.CheckReviewSystemPrompt),
            new("user",   ChecksService.BuildReviewPrompt(checks, diff)),
        };

        string answer;
        try
        {
            var result = await client.SendChatAsync(
                ModelRouter.Resolve(config, ModelRole.Chat), history, EmptyToolRegistry.Instance, null, ct);
            answer = result.TextContent;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Strings.MsgError(ex.Message)); }

        // Anchors come from the very text the model was shown, so a location is checked against
        // what the model could actually see — not against the working tree, which may have moved.
        return new(CheckReviewParser.Render(CheckReviewParser.Parse(answer, DiffAnchors.Parse(diff))));
    }
}
