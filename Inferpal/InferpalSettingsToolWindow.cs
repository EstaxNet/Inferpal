using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
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
    private readonly ProjectIndexService _index;
    private readonly Services.Docs.DocsIndexService _docs;
    private readonly VsContextHolder _context;

    public InferpalSettingsToolWindow(
        VisualStudioExtensibility extensibility,
        InferpalConfig config,
        IInferenceProvider client,
        McpToolService mcp,
        ProjectIndexService index,
        Services.Docs.DocsIndexService docs,
        VsContextHolder context)
        : base(extensibility)
    {
        _config  = config;
        _client  = client;
        _mcp     = mcp;
        _index   = index;
        _docs    = docs;
        _context = context;
        Title    = Strings.SettingsWindowTitle;
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.Floating,
    };

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
    {
        // The workspace root the approval service reads the team rules from: the table shows the same file.
        var live = new SettingsLiveSources(_index, _docs,
            ConversationUsage: () => _context.ConversationUsage?.Invoke() ?? Task.FromResult<Services.Presentation.XRayPanelModel?>(null),
            RepoInstructions: async () => _context.RepoInstructions is { } rows ? await rows() : null,
            OpenXray: OpenXrayAsync,
            OpenFile: OpenFileAsync,
            ToolList: async () => _context.ToolList is { } list ? await list() : null,
            SetTool: async (key, off) => _context.SetTool is { } set ? await set(key, off) : null);
        var data = new InferpalSettingsData(_config, _client, Extensibility, _mcp, () => _index.RootDir, live);
        // A page asked for before the window existed (a cut turn's "Open time limits"), and the way to ask it later.
        if (_context.SettingsPage is { } asked)
        {
            _context.SettingsPage = null;
            data.ShowPage(asked.Page, asked.Section);
        }
        _context.ShowSettingsPage = data.ShowPage;
        // Gone with its window: a later "Open time limits" then builds the next one on its page.
        data.OnDetached = () =>
        {
            if (_context.ShowSettingsPage == data.ShowPage) _context.ShowSettingsPage = null;
            if (_data == data) _data = null;
        };
        _data = data;
        return Task.FromResult<IRemoteUserControl>(new InferpalSettingsContent(data));
    }

    /// <summary>The form the window shows now.</summary>
    private InferpalSettingsData? _data;

    /// <summary>A key shown is masked again as the window goes, as its hint says — whether Visual Studio then disposes
    /// the form or only hides it (a docked tab left for another).</summary>
    public override Task OnHideAsync(CancellationToken cancellationToken)
    {
        if (_data is { } data) data.SynchronizationContext.Post(_ => data.MaskShownKeys(), null);
        return base.OnHideAsync(cancellationToken);
    }

    /// <summary>The window shown again: closing it only hid it, and its form follows the file now
    /// (<see cref="InferpalSettingsData.ShowAgain"/>).</summary>
    public override Task OnShowAsync(CancellationToken cancellationToken)
    {
        if (_data is { } data) data.SynchronizationContext.Post(_ => data.ShowAgain(), null);
        return base.OnShowAsync(cancellationToken);
    }

    /// <summary>The settings' "Open Context X-Ray": the chat window, shown, with its panel open.</summary>
    private async Task OpenXrayAsync()
    {
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, CancellationToken.None);
        if (_context.OpenXray is { } open) await open();
    }

    /// <summary>A project file opens in the editor; a folder (the rules) in the file explorer.</summary>
    private async Task OpenFileAsync(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            else
                await Extensibility.Documents().OpenTextDocumentAsync(new Uri(path), CancellationToken.None);
        }
        catch (Exception ex) { Diagnostics.Swallow("Settings.OpenFile", ex); }
    }
}
