using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Mcp;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using VsToolWindow = Microsoft.VisualStudio.Extensibility.ToolWindows.ToolWindow;

namespace Inferpal;

[VisualStudioContribution]
internal class InferpalSettingsToolWindow : VsToolWindow
{
    private readonly InferpalConfig _config;
    private readonly IInferenceProvider _client;
    private readonly McpToolService    _mcp;

    public InferpalSettingsToolWindow(
        VisualStudioExtensibility extensibility,
        InferpalConfig config,
        IInferenceProvider client,
        McpToolService mcp)
        : base(extensibility)
    {
        _config = config;
        _client = client;
        _mcp    = mcp;
        Title   = "Inferpal Settings";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.Floating,
    };

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
    {
        var data = new InferpalSettingsData(_config, _client, Extensibility, _mcp);
        return Task.FromResult<IRemoteUserControl>(new InferpalSettingsContent(data));
    }
}
