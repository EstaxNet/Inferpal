using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

internal class InferpalSettingsContent : RemoteUserControl
{
    private readonly InferpalSettingsData _data;

    public InferpalSettingsContent(InferpalSettingsData data)
        : base(data, data.SynchronizationContext)
    {
        _data = data;
    }

    public override async Task ControlLoadedAsync(CancellationToken ct)
    {
        await base.ControlLoadedAsync(ct);
        _data.SynchronizationContext.Post(_ => _data.ApplyLabels(), null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _data.Detach();   // unsubscribe from the shared config's live-sync event
        base.Dispose(disposing);
    }
}
