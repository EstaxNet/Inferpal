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
        // No default key: Alt+B opens the Build menu in English Visual Studio, Alt+O the Tools menu ("Outils") in French.
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
