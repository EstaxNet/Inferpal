using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/commit</c> (propose a message from the diff) and <c>/commit-exec</c>
/// (run the commit).
/// </summary>
/// <remarks>
/// <para>
/// The judgement calls both front-ends must share live here: which diff to describe, which model
/// writes the message, whether to auto-stage before committing, and how the message is escaped.
/// What is left to the caller is presentation — VS streams the proposal into a bubble and fills
/// its prompt box, the host returns a <c>setPrompt</c> effect.
/// </para>
/// <para>
/// <b>Nothing is committed without the user seeing the message.</b> <c>/commit</c> only proposes:
/// it pre-fills <c>/commit-exec &lt;message&gt;</c> and stops. Chaining the two automatically would
/// turn a suggestion into an action taken on the user's repository on the strength of a model's
/// sentence.
/// </para>
/// </remarks>
internal static class CommitCommandHandler
{
    /// <param name="Message">Terminal info to display when there is nothing to propose.</param>
    /// <param name="Notice">Shown before the proposal — e.g. "nothing staged, using the working tree".</param>
    /// <param name="Proposal">The commit message; null when none could be produced.</param>
    internal readonly record struct CommitProposal(string? Message, string? Notice, string? Proposal);

    /// <param name="Ok">Exit code 0.</param>
    /// <param name="Output">git's combined output, for the tool bubble.</param>
    internal readonly record struct CommitExecResult(bool Ok, string Output);

    /// <summary><c>/commit</c> — describe the current change, without committing anything.</summary>
    /// <param name="onToken">Streamed tokens of the proposal; null = no streaming.</param>
    public static async Task<CommitProposal> ProposeAsync(
        IInferenceProvider client, InferpalConfig config, GitRunner git,
        Action<string>? onToken, CancellationToken ct)
    {
        var staged = (await git("diff --staged", ct)).Output;

        string diffContext;
        string? notice = null;
        if (string.IsNullOrWhiteSpace(staged))
        {
            var status = (await git("status --short", ct)).Output;
            if (string.IsNullOrWhiteSpace(status))
                return new(Strings.CommitNothingToCommit, null, null);

            var unstaged = (await git("diff", ct)).Output;
            diffContext  = GitCommitPolicy.BuildUnstagedContext(status, unstaged);
            notice       = Strings.CommitNothingStaged;
        }
        else
        {
            diffContext = GitCommitPolicy.BuildStagedContext(staged);
        }

        diffContext = GitCommitPolicy.CapDiff(diffContext);

        try
        {
            var result = await client.RunAgentAsync(
                model:   await ModelRouter.ResolveUtilityAsync(config, client, ct),
                history: GitCommitPolicy.BuildProposalRequest(diffContext),
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: token => onToken?.Invoke(token),
                ct:      ct);

            // Think tags are stripped so reasoning-model output never lands in the commit message.
            var proposed = GitCommitPolicy.CleanProposal(result.FinalResponse);
            return new(null, notice, string.IsNullOrWhiteSpace(proposed) ? null : proposed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Strings.MsgError(ex.Message), notice, null); }
    }

    /// <summary><c>/commit-exec &lt;message&gt;</c> — stage if needed, then commit.</summary>
    /// <remarks>
    /// Auto-staging is limited to <c>git add -u</c>: tracked files that changed. Untracked files
    /// are left alone — sweeping in whatever happens to sit in the working tree is how a build
    /// output or a secrets file gets committed by accident.
    /// </remarks>
    public static async Task<CommitExecResult> ExecuteAsync(
        string message, GitRunner git, CancellationToken ct)
    {
        var stagedFiles = (await git("diff --staged --name-only", ct)).Output;
        if (string.IsNullOrWhiteSpace(stagedFiles))
            await git("add -u", ct);

        var (output, exitCode) = await git(
            $"commit -m \"{GitCommitPolicy.EscapeMessage(message)}\"", ct);

        return new(exitCode == 0, string.IsNullOrWhiteSpace(output) ? "(no output)" : output);
    }
}
