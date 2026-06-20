using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

internal abstract class SelectionCommandBase : Command
{
    private const int MaxCodeChars = 8_000;

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

                if (!sel.IsEmpty)
                {
                    // Selection present → attach only the selected code snippet.
                    rawCode     = sel.Extent.CopyToString();
                    attachLabel = $"Selection ({fileName})";
                }
                else
                {
                    // No selection → attach the whole file (original behaviour).
                    rawCode     = view.Document.Text.CopyToString();
                    attachLabel = fileName;
                }

                if (rawCode.Length > MaxCodeChars)
                    rawCode = rawCode[..MaxCodeChars] + "\n…(truncated)";
            }
        }
        catch { }

        if (string.IsNullOrEmpty(rawCode)) return;

        var prompt = BuildPrompt(fileName);
        _contextHolder.SetPendingPrompt(prompt, _config.CodeActionsModel,
            attachLabel: attachLabel, attachContent: rawCode);
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
