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

    protected SelectionCommandBase(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
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

                var excerpt = CodeExcerpt.Of(rawCode, CodeExcerpt.BudgetFor(_config.ContextWindowSize));
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
