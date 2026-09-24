using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

internal abstract class SelectionCommandBase : Command
{
    protected readonly VsContextHolder    _contextHolder;
    private   readonly InferpalConfig  _config;
    private   readonly IInferenceProvider _client;

    protected SelectionCommandBase(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config,
                                   IInferenceProvider client)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
        _client        = client;
    }

    /// <summary>Builds the instruction prompt for this code action (no code block — code is attached separately).</summary>
    protected abstract string BuildPrompt(string fileName);

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;

        string fileName    = string.Empty;
        string rawCode     = string.Empty;
        string attachLabel = string.Empty;

        try
        {
            // Prefer LatestView (snapshot captured before the context menu opened)
            // over GetActiveTextViewAsync whose IClientContext may no longer carry
            // the text selection by the time the menu item is clicked.
            var view = _contextHolder.LatestView
                    ?? await Extensibility.Editor().GetActiveTextViewAsync(context, ct);

            if (view is not null)
            {
                fileName = Path.GetFileName(view.Document.Uri.LocalPath);
                var sel  = view.Selection;

                // Selection present → attach only the selected code; none → the whole file.
                rawCode     = !sel.IsEmpty ? sel.Extent.CopyToString() : view.Document.Text.CopyToString();
                attachLabel = CodeExcerpt.SourceLabel(fileName, selection: !sel.IsEmpty);

                // Sized for the window the code-actions model (the one the pending prompt names) REALLY loaded.
                var window  = await Services.Agent.ContextManager.EffectiveWindowAsync(
                    _config, _client, ModelRouter.Resolve(_config, ModelRole.CodeActions), ct);
                var excerpt = CodeExcerpt.Of(rawCode, CodeExcerpt.BudgetFor(window));
                rawCode     = excerpt.Text;
                attachLabel = excerpt.Label(attachLabel);
            }
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("SelectionCommand.Run", ex); }

        if (string.IsNullOrEmpty(rawCode)) return;

        var prompt = BuildPrompt(fileName);
        _contextHolder.SetPendingPrompt(prompt, _config.CodeActionsModel,
            attachLabel: attachLabel, attachContent: rawCode);
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
