using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using VsToolWindow = Microsoft.VisualStudio.Extensibility.ToolWindows.ToolWindow;

namespace Inferpal;

[VisualStudioContribution]
internal class InferpalToolWindow : VsToolWindow
{
    private readonly IInferenceProvider     _client;
    private readonly ToolRegistry          _tools;
    private readonly InferpalConfig     _config;
    private readonly VsContextHolder       _contextHolder;
    private readonly ProjectIndexService   _indexService;
    private readonly ModelLifetimeService  _lifetimeService;
    private readonly VsBuildMonitor        _buildMonitor;
    private readonly DocsIndexService      _docsIndex;

    public InferpalToolWindow(
        VisualStudioExtensibility extensibility,
        IInferenceProvider        client,
        ToolRegistry              tools,
        InferpalConfig         config,
        VsContextHolder           contextHolder,
        ProjectIndexService       indexService,
        ModelLifetimeService      lifetimeService,
        VsBuildMonitor            buildMonitor,
        DocsIndexService          docsIndex)
        : base(extensibility)
    {
        _client          = client;
        _tools           = tools;
        _config          = config;
        _contextHolder   = contextHolder;
        _indexService    = indexService;
        _lifetimeService = lifetimeService;
        _buildMonitor    = buildMonitor;
        _docsIndex       = docsIndex;
        Title            = "Inferpal";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    // Singleton data: created once so the PendingPromptAvailable subscription is never
    // duplicated.  Without this, closing and reopening the panel leaves an old (now
    // invisible) InferpalToolWindowData subscribed to the event; it consumes the pending
    // model before the new instance can, causing code actions to fall back to DefaultModel.
    private InferpalToolWindowData? _data;

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
    {
        _data ??= new InferpalToolWindowData(_client, _tools, _config, Extensibility, _contextHolder, _indexService, _lifetimeService, _buildMonitor, _docsIndex);
        return Task.FromResult<IRemoteUserControl>(new InferpalToolWindowContent(_data));
    }
}
