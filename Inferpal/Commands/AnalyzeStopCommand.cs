using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Commands;

/// <summary>
/// "Analyze with Inferpal" in the Debug menu and in the call stack's and the variables' context menus: the stop as
/// <c>@debugger</c> renders it and the code around each of the user's frames, asked as a code action — without tools,
/// so nothing runs. Which line was chosen in those windows is not read (no API gives it): it is the whole stop. VS Code's
/// call stack and variables views ask the same of the host.
/// </summary>
[VisualStudioContribution]
internal class AnalyzeStopCommand : Command
{
    /// <summary>The Debug menu's last group: below the commands that start, step and stop.</summary>
    private const ushort DebugMenuPriority = 0xFF00;

    private readonly VsContextHolder _contextHolder;
    private readonly InferpalConfig _config;
    private readonly IInferenceProvider _client;
    private readonly Services.Debugging.IDebugSession _debug;
    private readonly Services.Editor.OpenDocumentOverlay _overlay;

    public AnalyzeStopCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config,
                              IInferenceProvider client, Services.Debugging.IDebugSession debug,
                              Services.Editor.OpenDocumentOverlay overlay)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
        _client        = client;
        _debug         = debug;
        _overlay       = overlay;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalAnalyzeStopCommand%")
    {
        Icon        = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
        // During a session, running or paused: a running one is said, not hidden.
        EnabledWhen = ActivationConstraint.UIContext(VsctMenus.DebuggingContext),
    };

    // One group per menu: GroupPlacement.VsctParent parents a group to one VSCT menu.
    [VisualStudioContribution]
    internal static CommandGroupConfiguration DebugMenuGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.ShlMainMenu, VsctMenus.DebugMenu, DebugMenuPriority))
        { Children = [GroupChild.Command<AnalyzeStopCommand>()] };

    [VisualStudioContribution]
    internal static CommandGroupConfiguration CallStackGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.VsDebugGroup, VsctMenus.CallStackContext, 0))
        { Children = [GroupChild.Command<AnalyzeStopCommand>()] };

    [VisualStudioContribution]
    internal static CommandGroupConfiguration LocalsGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.VsDebugGroup, VsctMenus.LocalsContext, 0))
        { Children = [GroupChild.Command<AnalyzeStopCommand>()] };

    [VisualStudioContribution]
    internal static CommandGroupConfiguration AutosGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.VsDebugGroup, VsctMenus.AutosContext, 0))
        { Children = [GroupChild.Command<AnalyzeStopCommand>()] };

    [VisualStudioContribution]
    internal static CommandGroupConfiguration WatchGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.VsDebugGroup, VsctMenus.WatchContext, 0))
        { Children = [GroupChild.Command<AnalyzeStopCommand>()] };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;

        // The ignore files before any file is read into the attachment.
        WorkspaceExclusions.Follow();
        // Sized for the window the code-actions model REALLY loaded: a code action has no tool to read the rest.
        var window   = await Services.Agent.ContextManager.EffectiveWindowAsync(
            _config, _client, ModelRouter.Resolve(_config, ModelRole.CodeActions), ct);
        var analysis = await Services.Debugging.DebugStopAnalysis.ForAsync(
            _debug, SolutionExtent.ActiveRoot(), _overlay, CodeExcerpt.BudgetFor(window), ct);
        if (analysis.Refusal is { } refusal)
        {
            // Visual Studio's stop comes from the in-process package: without it, "not paused" is not known.
            if (analysis.NothingPaused && Services.Signals.InProcAliveSignal.TryRead() is not { HasPackage: true })
                refusal = Strings.AnalyzeStopNoInProc;
            await Extensibility.Shell().ShowPromptAsync(refusal, PromptOptions.OK, ct);
            return;
        }

        // A code action, as the editor's context menu sends one: the code-actions model, without tools ("" = the default
        // model, still without tools).
        _contextHolder.SetPendingPrompt(analysis.Question, modelOverride: _config.CodeActionsModel,
                                        attachLabel: analysis.Chip, attachContent: analysis.Content);
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
