using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.Notifications;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.ToolWindow;

namespace Inferpal.Commands;

/// <summary>
/// "Review a Commit…" (Tools › Inferpal): a commit picked from the recent ones of the current branch — or a branch, tag or
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

        // The commits of the repository the chat's /check reads: the solution or folder open, else the open documents'.
        var (commits, failure) = await Services.Commands.ReviewCommits.ReadAsync(GitProcess.For(_contextHolder.ProjectRoot()), ct);

        var data = new ReviewCommitDialogData(commits, failure ?? string.Empty);
        using var control = new ReviewCommitDialogControl(data);
        using var close = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dialog = Extensibility.Shell().ShowDialogAsync(
            control, Strings.ReviewCommitTitle, new DialogOption(DialogButton.None, DialogResult.None), close.Token);

        // A button completes the choice and the token closes the dialog; dismissed otherwise, nothing is reviewed.
        var first = await Task.WhenAny(data.Chosen, dialog);
        if (first == dialog)
        {
            await dialog;
            return;
        }
        var chosen = await data.Chosen;
        await close.CancelAsync();
        try { await dialog; } catch (OperationCanceledException) { }

        if (chosen is null) return;
        _contextHolder.SetPendingPrompt($"/check {chosen}");
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
