using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Inferpal.Services;
using Inferpal.ToolWindow;

namespace Inferpal.Commands;

/// <summary>
/// VS menu command (Alt+M) — posts "/map" into the chat window and opens it.
/// The slash-command handler in InferpalToolWindowData then invokes
/// the <c>generate_project_map</c> tool.
/// </summary>
[VisualStudioContribution]
internal class MapCommand : Command
{
    private readonly VsContextHolder _contextHolder;

    public MapCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalMapCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
        Shortcuts =
        [
            new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.M),
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;

        // Posting "/map" as the pending prompt lets the tool-window's slash-command
        // router pick it up and invoke generate_project_map automatically.
        _contextHolder.SetPendingPrompt("/map");
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
