using Inferpal.Config;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Commands;

/// <summary>
/// Base for editor context-menu code actions that <b>apply their result directly to the
/// document</b> — in place, like "Edit with AI" — instead of streaming an answer into the
/// chat. Used by Refactor / Fix / Add-docs. The actual pipeline lives in
/// <see cref="InPlaceCodeEdit"/> (shared with the equivalent slash commands).
/// </summary>
internal abstract class InPlaceCodeActionBase : Command
{
    protected readonly VsContextHolder    _contextHolder;
    private   readonly InferpalConfig     _config;
    private   readonly IInferenceProvider _client;

    protected InPlaceCodeActionBase(
        VisualStudioExtensibility extensibility,
        VsContextHolder           contextHolder,
        InferpalConfig            config,
        IInferenceProvider        client)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
        _client        = client;
    }

    /// <summary>System prompt describing the transformation. Must instruct "reply with code only".</summary>
    protected abstract string SystemPrompt { get; }

    /// <summary>Short instruction line placed before the code in the user message.</summary>
    protected abstract string Instruction { get; }

    /// <summary>Localized message shown when the model judges the action a no-op (code already good).</summary>
    protected abstract string NoChangeMessage { get; }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;

        // Prefer the snapshot captured before the context menu opened (LatestView): the live
        // IClientContext may no longer carry the selection by the time we run.
        var view = _contextHolder.LatestView
                ?? await Extensibility.Editor().GetActiveTextViewAsync(context, ct);
        if (view is null) return;

        var outcome = await InPlaceCodeEdit.RunAsync(
            Extensibility, view, _client, ResolveModel(), SystemPrompt, Instruction, ct);

        // No chat to write to here — surface the "nothing to do" verdict as a dismissable prompt so
        // the unchanged document doesn't look like the command silently failed.
        if (outcome == InPlaceEditOutcome.NoChangeNeeded)
            await Extensibility.Shell().ShowPromptAsync(NoChangeMessage, PromptOptions.OK, ct);
    }

    private string ResolveModel() =>
        string.IsNullOrEmpty(_config.CodeActionsModel) ? _config.DefaultModel : _config.CodeActionsModel;
}
