using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Commands;

/// <summary>
/// The "Ask Inferpal" submenu of Solution Explorer's project, folder and file context menus — Explain, Review Changes,
/// Attach to Chat. VS Code's file explorer has the same three.
/// </summary>
internal static class SolutionExplorerMenu
{
    [VisualStudioContribution]
    internal static MenuConfiguration Submenu { get; } = new("%ContextMenuAskInferpal%")
    {
        Id = "Inferpal.Commands.SolutionExplorerMenu.Submenu",
        Children =
        [
            MenuChild.Command<ExplainItemCommand>(),
            MenuChild.Command<ReviewItemCommand>(),
            MenuChild.Command<AttachItemCommand>(),
        ],
    };

    // One group per menu: GroupPlacement.VsctParent parents a group to one VSCT menu.
    [VisualStudioContribution]
    internal static CommandGroupConfiguration ProjectGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.ProjectContext, 0)) { Children = [GroupChild.Menu(Submenu)] };

    [VisualStudioContribution]
    internal static CommandGroupConfiguration FolderGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.FolderContext, 0)) { Children = [GroupChild.Menu(Submenu)] };

    [VisualStudioContribution]
    internal static CommandGroupConfiguration ItemGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.ItemContext, 0)) { Children = [GroupChild.Menu(Submenu)] };

    /// <summary>The item chosen in Solution Explorer — the client context carries it out of process — or <c>null</c>,
    /// said: a node with no file behind it (a solution folder, a virtual node) has no path.</summary>
    internal static async Task<string?> SelectedPathAsync(VisualStudioExtensibility vs, IClientContext context, CancellationToken ct)
    {
        string? path = null;
        try
        {
            if (await context.GetSelectedPathAsync(ct) is { IsFile: true } uri) path = uri.LocalPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("SolutionExplorer.SelectedPath", ex); }
        if (path is null) await vs.Shell().ShowPromptAsync(Strings.ExplorerNoSelection, PromptOptions.OK, ct);
        return path;
    }
}

/// <summary>"Explain" on a project, a folder or a file: the Core's question (<see cref="ExplorerExplain"/>), asked as a code
/// action — the code-actions model, without tools, the attachment sized for its window.</summary>
[VisualStudioContribution]
internal class ExplainItemCommand : Command
{
    private readonly VsContextHolder _contextHolder;
    private readonly InferpalConfig _config;
    private readonly IInferenceProvider _client;
    private readonly Services.Editor.OpenDocumentOverlay _overlay;

    public ExplainItemCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config,
                              IInferenceProvider client, Services.Editor.OpenDocumentOverlay overlay)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
        _client        = client;
        _overlay       = overlay;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalExplainItemCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.StatusHelp, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        if (await SolutionExplorerMenu.SelectedPathAsync(Extensibility, context, ct) is not { } path) return;

        // The ignore files before any file is read into the attachment.
        WorkspaceExclusions.Follow();
        var window = await Services.Agent.ContextManager.EffectiveWindowAsync(
            _config, _client, ModelRouter.Resolve(_config, ModelRole.CodeActions), ct);
        var ask = await ExplorerExplain.ForAsync(ExplorerExplain.ItemOf(path), SolutionExtent.ActiveRoot(), _overlay,
                                                 CodeExcerpt.BudgetFor(window), ct);
        if (ask.Refusal is { } refusal)
        {
            await Extensibility.Shell().ShowPromptAsync(refusal, PromptOptions.OK, ct);
            return;
        }
        // A code action ("" = the default model, still without tools).
        _contextHolder.SetPendingPrompt(ask.Question, modelOverride: _config.CodeActionsModel, attachLabel: ask.Chip,
                                        attachContent: ask.Content);
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}

/// <summary>"Review Changes" on a project, a folder or a file: <c>/check</c> on it, as if typed — a project is its
/// folder.</summary>
[VisualStudioContribution]
internal class ReviewItemCommand : Command
{
    private readonly VsContextHolder _contextHolder;

    public ReviewItemCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder) : base(extensibility)
    {
        _contextHolder = contextHolder;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalReviewItemCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.Checklist, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        if (await SolutionExplorerMenu.SelectedPathAsync(Extensibility, context, ct) is not { } path) return;

        // A slash command: the chat window routes it, the model never reads it as a question.
        _contextHolder.SetPendingPrompt($"/check \"{ExplorerExplain.ItemOf(path)}\"");
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}

/// <summary>"Attach to Chat" on a project, a folder or a file: the chip <c>@folder</c> or <c>@file</c> makes, in the
/// composer — nothing sent; a project is its folder.</summary>
[VisualStudioContribution]
internal class AttachItemCommand : Command
{
    private readonly VsContextHolder _contextHolder;

    public AttachItemCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder) : base(extensibility)
    {
        _contextHolder = contextHolder;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalAttachItemCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.Attach, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        if (await SolutionExplorerMenu.SelectedPathAsync(Extensibility, context, ct) is not { } path) return;

        // The ignore files before the chat window reads the file or the folder for the chip.
        WorkspaceExclusions.Follow();
        _contextHolder.SetPendingAttach(ExplorerExplain.ItemOf(path));
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
