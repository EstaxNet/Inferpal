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
/// ⚠ Theme colours and the Edit/Delete tooltips are deliberately NOT per-row properties, and the
/// reason is that they are <b>shared</b>: one theme, one language, for every row. They live on the
/// parent's theme/language sub-tree and the template binds them off the root data context via
/// <c>ElementName=root</c>, so a theme or language change reaches every row at once without
/// touching any of them. See <c>InferpalSettingsData.ApplyRowTheme</c>.
/// <para>
/// ⚠ It is <b>not</b> that a per-row property cannot change after the row is bound: it can, and the
/// product depends on it — the chat streams by mutating <c>ChatMessageItem.Content</c> on an item
/// already in <c>Messages</c>, and a theme switch re-themes those same items in place
/// (<c>UpdateMessageBubbles</c>). <see cref="StatusText"/> and <see cref="AuthRequired"/> below are
/// written on every status refresh for exactly that reason.
/// </para>
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
    private string _stateKey   = "notStarted";
    private string _cause      = string.Empty;
    private bool   _showRetry;
    private bool   _hasCause;

    internal Action<McpServerRow>? OnEdit;
    internal Action<McpServerRow>? OnDelete;
    internal Func<McpServerRow, Task>? OnAuthorize;
    internal Func<Task>? OnRetry;

    public McpServerRow()
    {
        EditCommand      = new AsyncCommand((_, _) => { OnEdit?.Invoke(this);   return Task.CompletedTask; });
        DeleteCommand    = new AsyncCommand((_, _) => { OnDelete?.Invoke(this); return Task.CompletedTask; });
        AuthorizeCommand = new AsyncCommand((_, _) => OnAuthorize?.Invoke(this) ?? Task.CompletedTask);
        RetryCommand     = new AsyncCommand((_, _) => OnRetry?.Invoke() ?? Task.CompletedTask);
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

    /// <summary>Command arguments, one per LINE: split on spaces, <c>C:\My Projects</c> became two arguments
    /// the first time the server was edited.</summary>
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

    /// <summary>What the server is doing, from <see cref="Services.Presentation.McpServerCards"/>.</summary>
    [DataMember] public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    /// <summary>True when this (HTTP) server awaits OAuth authorization — shows the Sign in button.</summary>
    [DataMember] public bool AuthRequired { get => _authRequired; set => SetProperty(ref _authRequired, value); }

    /// <summary>The card state — connected | signIn | failed | off | notStarted — which picks the dot's color.</summary>
    [DataMember] public string StateKey { get => _stateKey; set => SetProperty(ref _stateKey, value); }

    /// <summary>Why the server does not run (its stderr, the refusal): what the user needs to fix it.</summary>
    [DataMember] public string Cause { get => _cause; set { if (SetProperty(ref _cause, value)) HasCause = value.Length > 0; } }

    [DataMember] public bool HasCause { get => _hasCause; private set => SetProperty(ref _hasCause, value); }

    /// <summary>True when the server did not start: shows Retry.</summary>
    [DataMember] public bool ShowRetry { get => _showRetry; set => SetProperty(ref _showRetry, value); }

    /// <summary>
    /// The definition this row was read from. Not a data member: it carries what the row does not show
    /// (the OAuth block), so rewriting the list from the rows keeps it.
    /// </summary>
    internal Services.Mcp.McpServerConfig? Source { get; set; }

    [DataMember] public AsyncCommand EditCommand      { get; }
    [DataMember] public AsyncCommand DeleteCommand    { get; }
    [DataMember] public AsyncCommand AuthorizeCommand { get; }
    [DataMember] public AsyncCommand RetryCommand     { get; }

    /// <summary>"stdio · command args" or "HTTP · url", as the VS Code card reads — an argument with a space
    /// quoted so the line reads as typed.</summary>
    private void RefreshSummary()
    {
        var args = ArgsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
        Summary = !string.IsNullOrWhiteSpace(Url)
            ? $"HTTP · {Url}"
            : $"stdio · {string.Join(' ', new[] { Command }.Concat(args)).Trim()}";
    }
}
