using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One row in the MCP servers list of the settings window. Holds the editable fields of a
/// single server plus its live connection status and per-row Edit/Delete commands.
/// </summary>
/// <remarks>
/// <see cref="OnEdit"/> / <see cref="OnDelete"/> are wired by the parent
/// <see cref="InferpalSettingsData"/> when the row is created (same callback pattern as
/// <see cref="ChatMessageItem.InitFixCallback"/>). Toggling <see cref="Enabled"/> raises
/// <c>PropertyChanged</c> so the parent can keep the JSON mirror in sync.
/// <para>
/// Theme colours and the Edit/Delete tooltips are deliberately NOT per-row properties: in the
/// Extensibility RemoteUI a property changed on a collection item *after* it has been added to
/// the bound <c>ObservableCollection</c> (i.e. after the initial snapshot) does not propagate to
/// the rendered template. Those values are pushed by the parent at row-construction time on the
/// theme/language sub-tree and the template binds them off the root data context via
/// <c>ElementName=root</c>, so they always reflect the live theme/language. See
/// <c>InferpalSettingsData.ApplyRowTheme</c>.
/// </para>
/// </remarks>
[DataContract]
internal sealed class McpServerRow : NotifyPropertyChangedObject
{
    private string _serverName = string.Empty;
    private string _command   = string.Empty;
    private string _argsText  = string.Empty;
    private string _envText   = string.Empty;
    private string _url        = string.Empty;
    private string _headersText = string.Empty;
    private bool   _enabled    = true;
    private string _summary    = string.Empty;
    private string _statusText = string.Empty;
    private bool   _authRequired;

    internal Action<McpServerRow>? OnEdit;
    internal Action<McpServerRow>? OnDelete;
    internal Func<McpServerRow, Task>? OnAuthorize;

    public McpServerRow()
    {
        EditCommand      = new AsyncCommand((_, _) => { OnEdit?.Invoke(this);   return Task.CompletedTask; });
        DeleteCommand    = new AsyncCommand((_, _) => { OnDelete?.Invoke(this); return Task.CompletedTask; });
        AuthorizeCommand = new AsyncCommand((_, _) => OnAuthorize?.Invoke(this) ?? Task.CompletedTask);
    }

    /// <summary>
    /// Server name (the JSON map key). Unique within the list. Deliberately NOT called <c>Name</c>:
    /// a per-item data member named <c>Name</c> is not surfaced to the RemoteUI item template (the
    /// chat list, which renders fine, never binds a <c>Name</c> member), so <c>{Binding Name}</c>
    /// rendered blank. <c>ServerName</c> binds normally.
    /// </summary>
    [DataMember] public string ServerName { get => _serverName; set { if (SetProperty(ref _serverName, value)) RefreshSummary(); } }

    /// <summary>Executable / command launched for the stdio transport.</summary>
    [DataMember] public string Command  { get => _command; set { if (SetProperty(ref _command, value)) RefreshSummary(); } }

    /// <summary>Command arguments, one per space (display/edit form).</summary>
    [DataMember] public string ArgsText { get => _argsText; set { if (SetProperty(ref _argsText, value)) RefreshSummary(); } }

    /// <summary>Environment variables, one <c>KEY=value</c> per line.</summary>
    [DataMember] public string EnvText  { get => _envText;  set => SetProperty(ref _envText, value); }

    /// <summary>Endpoint URL for the Streamable HTTP transport. Non-empty ⇒ this is an HTTP server
    /// (carried through for list⇄JSON round-trips; HTTP servers are edited via the JSON view).</summary>
    [DataMember] public string Url { get => _url; set { if (SetProperty(ref _url, value)) RefreshSummary(); } }

    /// <summary>HTTP headers, one <c>Key=value</c> per line. Round-tripped opaquely with the row.</summary>
    [DataMember] public string HeadersText { get => _headersText; set => SetProperty(ref _headersText, value); }

    /// <summary>Whether this server is spawned. Bound TwoWay to the row checkbox.</summary>
    [DataMember] public bool   Enabled  { get => _enabled;  set => SetProperty(ref _enabled, value); }

    /// <summary>One-line "command + first args" preview shown next to the name.</summary>
    [DataMember] public string Summary  { get => _summary;  set => SetProperty(ref _summary, value); }

    /// <summary>Connection result for this server (✓ N tools / ✗ error / — disabled / 🔒 auth).</summary>
    [DataMember] public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    /// <summary>True when this (HTTP) server awaits OAuth authorization — shows the Authorize button.</summary>
    [DataMember] public bool AuthRequired { get => _authRequired; set => SetProperty(ref _authRequired, value); }

    [DataMember] public AsyncCommand EditCommand      { get; }
    [DataMember] public AsyncCommand DeleteCommand    { get; }
    [DataMember] public AsyncCommand AuthorizeCommand { get; }

    private void RefreshSummary()
    {
        var s = !string.IsNullOrWhiteSpace(Url)
            ? Url
            : string.IsNullOrWhiteSpace(ArgsText) ? Command : $"{Command} {ArgsText}";
        Summary = s.Length > 70 ? s[..70] + "…" : s;
    }
}
