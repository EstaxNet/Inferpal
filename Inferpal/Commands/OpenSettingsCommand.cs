using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

[VisualStudioContribution]
internal class OpenSettingsCommand : Command
{
    public OpenSettingsCommand(VisualStudioExtensibility extensibility)
        : base(extensibility) { }

    public override CommandConfiguration CommandConfiguration => new("%InferpalSettingsCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.Settings, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        await Extensibility.Shell().ShowToolWindowAsync<InferpalSettingsToolWindow>(activate: true, ct);
    }
}
