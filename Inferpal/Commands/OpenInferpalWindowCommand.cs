using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

[VisualStudioContribution]
internal class OpenInferpalWindowCommand : Command
{
    private readonly VsContextHolder _contextHolder;

    public OpenInferpalWindowCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalChatCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
        Shortcuts =
        [
            new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.O),
            new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.B),
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
