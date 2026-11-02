using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.Notifications;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.ToolWindow;

namespace Inferpal.Commands;

/// <summary>
/// "Review a Commit…" (the chat window's ⋯ menu, which shares <see cref="ChooseAsync"/>; the command itself has no
/// menu): a commit picked from the recent ones of the current branch — or a branch, tag or
/// range typed in the dialog — reviewed by <c>/check</c> in the chat, as typing it does; VS Code's palette has the same
/// command.
/// </summary>
[VisualStudioContribution]
internal class ReviewCommitCommand : Command
{
    private readonly VsContextHolder _contextHolder;

    public ReviewCommitCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalReviewCommitCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        if (await ChooseAsync(Extensibility, _contextHolder.ProjectRoot(), ct) is not { } chosen) return;
        _contextHolder.SetPendingPrompt($"/check {chosen}");
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }

    /// <summary>
    /// The dialog: what the person chose to review (a commit, a branch, a tag, a range), or <c>null</c> when dismissed.
    /// Shared with the chat window's ⋯ menu.
    /// </summary>
    /// <param name="projectRoot">The repository the chat's <c>/check</c> reads: the solution or folder open, else the open
    /// documents'.</param>
    internal static async Task<string?> ChooseAsync(VisualStudioExtensibility vs, string? projectRoot, CancellationToken ct)
    {
        var (commits, failure) = await Services.Commands.ReviewCommits.ReadAsync(GitProcess.For(projectRoot), ct);

        var data = new ReviewCommitDialogData(commits, failure ?? string.Empty);
        using var control = new ReviewCommitDialogControl(data);
        using var close = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dialog = vs.Shell().ShowDialogAsync(
            control, Strings.ReviewCommitTitle, new DialogOption(DialogButton.None, DialogResult.None), close.Token);

        // A button completes the choice and the token closes the dialog; dismissed otherwise, nothing is reviewed.
        var first = await Task.WhenAny(data.Chosen, dialog);
        if (first == dialog)
        {
            await dialog;
            return null;
        }
        var chosen = await data.Chosen;
        await close.CancelAsync();
        try { await dialog; } catch (OperationCanceledException) { }
        return chosen;
    }
}
