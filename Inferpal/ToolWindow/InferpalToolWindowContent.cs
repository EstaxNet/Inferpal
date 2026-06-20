using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal;

internal class InferpalToolWindowContent : RemoteUserControl
{
    private readonly InferpalToolWindowData _data;

    public InferpalToolWindowContent(InferpalToolWindowData data)
        : base(data, data.SynchronizationContext)
    {
        _data = data;
    }

    public override async Task ControlLoadedAsync(CancellationToken ct)
    {
        await base.ControlLoadedAsync(ct);
        _data.SynchronizationContext.Post(_ => _data.ApplyLabels(), null);
    }
}
