using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services.Inference;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>One hop of the Test, as the settings window draws it.</summary>
[DataContract]
internal sealed class ProxyHopItem
{
    [DataMember] public string Glyph      { get; init; } = string.Empty;
    [DataMember] public string GlyphColor { get; init; } = string.Empty;
    [DataMember] public string Background { get; init; } = string.Empty;
    [DataMember] public string Title      { get; init; } = string.Empty;
    [DataMember] public string Detail     { get; init; } = string.Empty;
    [DataMember] public bool HasDetail    { get; init; }
    [DataMember] public string Checks     { get; init; } = string.Empty;
    [DataMember] public bool HasChecks    { get; init; }

    /// <summary>The hop in the theme's colours: passed, failed, not reached, and the failed row's surface.</summary>
    internal static ProxyHopItem Of(ProxyHop hop, string passed, string failed, string idle, string failedBg) => new()
    {
        // Segoe MDL2: CompletedSolid, StatusErrorFull, CircleRing.
        Glyph      = hop.State switch { HopState.Passed => "\uEC61", HopState.Failed => "\uEB90", _ => "\uEA3A" },
        GlyphColor = hop.State switch { HopState.Passed => passed, HopState.Failed => failed, _ => idle },
        Background = hop.State == HopState.Failed ? failedBg : "Transparent",
        Title      = hop.Title,
        Detail     = hop.Detail ?? string.Empty,
        HasDetail  = !string.IsNullOrEmpty(hop.Detail),
        Checks     = string.Join("\n", (hop.Checks ?? []).Select(c => "•  " + c)),
        HasChecks  = hop.Checks is { Count: > 0 },
    };
}

/// <summary>The words of the proxy access block, in the interface language.</summary>
[DataContract]
internal sealed class ProxyTexts
{
    [DataMember] public string Title { get; init; } = Strings.ProxySectionTitle;
    [DataMember] public string Intro { get; init; } = Strings.ProxySectionIntro;
    [DataMember] public string Question { get; init; } = Strings.ProxyQuestion;
    [DataMember] public string None { get; init; } = Strings.ProxyMethodNone;
    [DataMember] public string NoneDesc { get; init; } = Strings.ProxyMethodNoneDesc;
    [DataMember] public string Basic { get; init; } = Strings.ProxyMethodBasic;
    [DataMember] public string BasicDesc { get; init; } = Strings.ProxyMethodBasicDesc;
    [DataMember] public string Bearer { get; init; } = Strings.ProxyMethodBearer;
    [DataMember] public string BearerDesc { get; init; } = Strings.ProxyMethodBearerDesc;
    [DataMember] public string Cloudflare { get; init; } = Strings.ProxyMethodCloudflare;
    [DataMember] public string CloudflareDesc { get; init; } = Strings.ProxyMethodCloudflareDesc;
    [DataMember] public string Custom { get; init; } = Strings.ProxyMethodCustom;
    [DataMember] public string CustomDesc { get; init; } = Strings.ProxyMethodCustomDesc;
    [DataMember] public string NotSure { get; init; } = Strings.ProxyNotSure;
    [DataMember] public string HowTo { get; init; } = Strings.ProxyHowToLink;
    [DataMember] public string User { get; init; } = Strings.ProxyUser;
    [DataMember] public string Password { get; init; } = Strings.ProxyPassword;
    [DataMember] public string BearerIntro { get; init; } = Strings.ProxyBearerIntro;
    [DataMember] public string Token { get; init; } = Strings.ProxyMethodBearer;
    [DataMember] public string CfIntro { get; init; } = Strings.ProxyCfIntro;
    [DataMember] public string CfSteps { get; init; } = $"1.  {Strings.ProxyCfStep1}\n2.  {Strings.ProxyCfStep2}\n3.  {Strings.ProxyCfStep3}";
    [DataMember] public string CfId { get; init; } = Strings.ProxyCfId;
    [DataMember] public string CfIdHint { get; init; } = Strings.ProxyCfIdHint;
    [DataMember] public string CfSecret { get; init; } = Strings.ProxyCfSecretLabel;
    [DataMember] public string CustomIntro { get; init; } = Strings.ProxyCustomIntro;
    [DataMember] public string SendsTitle { get; init; } = Strings.ProxySendsTitle;
    [DataMember] public string SendsWhen { get; init; } = Strings.ProxySendsWhen;
    [DataMember] public string SendsHttps { get; init; } = Strings.ProxySendsHttps;
    [DataMember] public string SendsHttp { get; init; } = Strings.ProxySendsHttp;
    [DataMember] public string EditRaw { get; init; } = Strings.ProxyEditRaw;
    [DataMember] public string TestTitle { get; init; } = Strings.ProxyTestTitle;
    [DataMember] public string WhatToCheck { get; init; } = Strings.ProxyWhatToCheck;
    [DataMember] public string MovedNone { get; init; } = Strings.ProxyMovedNone;
    [DataMember] public string MovedNew { get; init; } = Strings.ProxyMovedNew;
    [DataMember] public string MovedKeep { get; init; } = Strings.ProxyMovedKeep;
}

/// <summary>
/// The Server and models page's proxy access block: the method the proxy asks for, its fields — the headers are built
/// from them, <see cref="RequestHeaders"/> stays what Save writes and the unsaved count compares —, what will be sent and
/// to whom, the Test said hop by hop, and the credentials kept with their proxy when the address moves.
/// </summary>
internal partial class InferpalSettingsData
{
    private string _proxyMethod = "none", _proxyUser = string.Empty, _proxyPassword = string.Empty, _proxyToken = string.Empty,
                   _proxyCfId = string.Empty, _proxyCfSecret = string.Empty, _proxyRaw = string.Empty,
                   _proxyBasicIntro = string.Empty, _proxySendsPreview = string.Empty, _proxySendsWhere = string.Empty,
                   _proxyLineWarning = string.Empty, _proxyMovedTitle = string.Empty, _proxyMovedBody = string.Empty,
                   _proxyMovedFor = string.Empty, _proxyMovedWas = string.Empty, _proxyMovedChoice = "none";
    private bool _proxyHttp, _proxyHopsVisible, _proxyMovedVisible, _proxyRawShown;
    /// <summary>The form's headers when another address showed the moved block: given back if the address returns.</summary>
    private string _proxyBeforeMove = string.Empty;
    private ProxyTexts _proxyTexts = new();
    /// <summary>Set while the fields are filled from the setting: writing them back would be a change nobody made.</summary>
    private bool _proxyLoading;

    /// <summary><c>none</c>, <c>basic</c>, <c>bearer</c>, <c>cloudflare</c> or <c>custom</c>: the card drawn selected.</summary>
    [DataMember] public string ProxyMethodSelected { get => _proxyMethod; set { if (SetProperty(ref _proxyMethod, value)) ProxyChanged(); } }
    [DataMember] public string ProxyUser     { get => _proxyUser;     set { if (SetProperty(ref _proxyUser,     value)) ProxyChanged(); } }
    [DataMember] public string ProxyPassword { get => _proxyPassword; set { if (SetProperty(ref _proxyPassword, value)) ProxyChanged(); } }
    [DataMember] public string ProxyToken    { get => _proxyToken;    set { if (SetProperty(ref _proxyToken,    value)) ProxyChanged(); } }
    [DataMember] public string ProxyCfId     { get => _proxyCfId;     set { if (SetProperty(ref _proxyCfId,     value)) ProxyChanged(); } }
    [DataMember] public string ProxyCfSecret { get => _proxyCfSecret; set { if (SetProperty(ref _proxyCfSecret, value)) ProxyChanged(); } }
    /// <summary>The text of <c>custom</c>: kept while another method is tried, so coming back loses nothing.</summary>
    [DataMember] public string ProxyRaw      { get => _proxyRaw;      set { if (SetProperty(ref _proxyRaw,      value)) ProxyChanged(); } }
    /// <summary>"Edit as raw headers": the setting itself, as Save writes it.</summary>
    [DataMember] public string ProxyRawHeaders
    {
        get => RequestHeaders ?? string.Empty;
        set
        {
            if (value == RequestHeaders) return;
            RequestHeaders = value;
            RaiseNotifyPropertyChangedEvent(nameof(ProxyRawHeaders));
            UpdateProxyState();
        }
    }

    [DataMember] public SecretFieldItem ProxyPasswordKey { get; private set; } = null!;
    [DataMember] public SecretFieldItem ProxyTokenKey    { get; private set; } = null!;
    [DataMember] public SecretFieldItem ProxyCfSecretKey { get; private set; } = null!;
    [DataMember] public AsyncCommand SetProxyMethodCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand ToggleProxyRawCommand  { get; private set; } = null!;
    [DataMember] public AsyncCommand SetProxyMovedCommand   { get; private set; } = null!;
    [DataMember] public AsyncCommand OpenProxyHowToCommand  { get; private set; } = null!;
    private string _proxyHowToFallback = string.Empty;
    /// <summary>The guide's address, said when no browser opened it.</summary>
    [DataMember] public string ProxyHowToFallback { get => _proxyHowToFallback; set => SetProperty(ref _proxyHowToFallback, value); }

    [DataMember] public ProxyTexts ProxyTexts      { get => _proxyTexts;        set => SetProperty(ref _proxyTexts,        value); }
    [DataMember] public string ProxyBasicIntro     { get => _proxyBasicIntro;   set => SetProperty(ref _proxyBasicIntro,   value); }
    /// <summary>The headers that will leave, their secrets masked; empty under <c>none</c>.</summary>
    [DataMember] public string ProxySendsPreview   { get => _proxySendsPreview; set => SetProperty(ref _proxySendsPreview, value); }
    [DataMember] public string ProxySendsWhere     { get => _proxySendsWhere;   set => SetProperty(ref _proxySendsWhere,   value); }
    [DataMember] public bool ProxySends            => _proxySendsPreview.Length > 0;
    /// <summary>The server address is http://: the credentials would travel readable.</summary>
    [DataMember] public bool ProxyHttp             { get => _proxyHttp;         set => SetProperty(ref _proxyHttp,         value); }
    [DataMember] public string ProxyLineWarning    { get => _proxyLineWarning;  set => SetProperty(ref _proxyLineWarning,  value); }
    [DataMember] public bool ProxyRawShown         { get => _proxyRawShown;     set => SetProperty(ref _proxyRawShown,     value); }
    [DataMember] public ObservableCollection<ProxyHopItem> ProxyHops { get; } = [];
    [DataMember] public bool ProxyHopsVisible      { get => _proxyHopsVisible;  set => SetProperty(ref _proxyHopsVisible,  value); }

    [DataMember] public bool ProxyMovedVisible     { get => _proxyMovedVisible; set => SetProperty(ref _proxyMovedVisible, value); }
    [DataMember] public string ProxyMovedTitle     { get => _proxyMovedTitle;   set => SetProperty(ref _proxyMovedTitle,   value); }
    [DataMember] public string ProxyMovedBody      { get => _proxyMovedBody;    set => SetProperty(ref _proxyMovedBody,    value); }
    [DataMember] public string ProxyMovedFor       { get => _proxyMovedFor;     set => SetProperty(ref _proxyMovedFor,     value); }
    [DataMember] public string ProxyMovedWas       { get => _proxyMovedWas;     set => SetProperty(ref _proxyMovedWas,     value); }
    /// <summary><c>none</c>, <c>new</c> or <c>keep</c>: what the credentials of the old proxy become at the new address.</summary>
    [DataMember] public string ProxyMovedChoice    { get => _proxyMovedChoice;  set => SetProperty(ref _proxyMovedChoice,  value); }

    /// <summary>The saved headers, as the form opened them.</summary>
    private string SavedHeaders => (string?)_opened["requestHeaders"] ?? string.Empty;

    /// <summary>The commands and the masked fields; called by the constructor.</summary>
    private void InitProxy()
    {
        ProxyPasswordKey = new(() => ProxyAccess.Read(SavedHeaders).Password, () => ProxyPassword, v => ProxyPassword = v, RunOnVMContextAsync);
        ProxyTokenKey    = new(() => ProxyAccess.Read(SavedHeaders).Token,    () => ProxyToken,    v => ProxyToken = v,    RunOnVMContextAsync);
        ProxyCfSecretKey = new(() => ProxyAccess.Read(SavedHeaders).ClientSecret, () => ProxyCfSecret, v => ProxyCfSecret = v, RunOnVMContextAsync);
        SetProxyMethodCommand = new AsyncCommand((p, _) => RunOnVMContextAsync(() =>
        {
            if (p is "none" or "basic" or "bearer" or "cloudflare" or "custom") ProxyMethodSelected = (string)p;
        }));
        ToggleProxyRawCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            ProxyRawShown = !ProxyRawShown;
            if (!ProxyRawShown) LoadProxy(RequestHeaders);   // back to the fields: read what was written as text
        }));
        OpenProxyHowToCommand = new AsyncCommand((_, _) =>
            WebLink.Open(Services.DocsLinks.ReverseProxy) ? Task.CompletedTask
            : RunOnVMContextAsync(() => ProxyHowToFallback = Strings.HelpDocumentation(Services.DocsLinks.ReverseProxy)));
        SetProxyMovedCommand = new AsyncCommand((p, _) => RunOnVMContextAsync(() =>
        {
            if (p is not ("none" or "new" or "keep")) return;
            ProxyMovedChoice = (string)p;
            LoadProxy(p switch { "keep" => SavedHeaders, _ => string.Empty });
            if (p is "new") ProxyMethodSelected = ProxyAccess.Read(SavedHeaders).Method is var m and not ProxyMethod.None ? Code(m) : "basic";
        }));
    }

    /// <summary>The block's words; part of <see cref="ApplyLabels"/>.</summary>
    private void ApplyProxyLabels()
    {
        ProxyTexts = new();
        ProxyPasswordKey.ApplyLabels(Strings.ProxyPassword, string.Empty);
        ProxyTokenKey.ApplyLabels(Strings.ProxyMethodBearer, string.Empty);
        ProxyCfSecretKey.ApplyLabels(Strings.ProxyCfSecretLabel, string.Empty);
        UpdateProxyState();
    }

    /// <summary>Fills the fields from <paramref name="headers"/> (the form opened, was cancelled, reloaded, or a choice).</summary>
    private void LoadProxy(string? headers)
    {
        _proxyLoading = true;
        ProxyHopsVisible = false;   // a Test of other headers says nothing of these
        try
        {
            var form = ProxyAccess.Read(headers);
            ProxyMethodSelected = Code(form.Method);
            ProxyUser = form.User; ProxyPassword = form.Password; ProxyToken = form.Token;
            ProxyCfId = form.ClientId; ProxyCfSecret = form.ClientSecret;
            ProxyRaw = form.Method == ProxyMethod.Custom ? form.Raw : string.Empty;
            RequestHeaders = (headers ?? string.Empty).Trim();
            ProxyRawShown = false;
        }
        finally { _proxyLoading = false; }
        RaiseNotifyPropertyChangedEvent(nameof(ProxyRawHeaders));
        ProxyPasswordKey?.Reset(); ProxyTokenKey?.Reset(); ProxyCfSecretKey?.Reset();
        UpdateProxyState();
    }

    private void ProxyChanged()
    {
        if (_proxyLoading) return;
        RequestHeaders = ProxyAccess.Write(new(MethodOf(_proxyMethod), _proxyUser, _proxyPassword, _proxyToken, _proxyCfId, _proxyCfSecret, _proxyRaw));
        RaiseNotifyPropertyChangedEvent(nameof(ProxyRawHeaders));
        UpdateProxyState();
    }

    /// <summary>The preview, the warnings and the moved block, from the form as it stands.</summary>
    private void UpdateProxyState()
    {
        var url = (BaseUrl ?? string.Empty).Trim();
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        ProxyBasicIntro = Strings.ProxyBasicIntro(host);
        var headers = ServerHeaders.Parse(RequestHeaders);
        ProxySendsPreview = string.Join("\n", headers.Select(h => h.Key + ": " + Masked(h.Key, h.Value)));
        RaiseNotifyPropertyChangedEvent(nameof(ProxySends));
        ProxySendsWhere = Strings.ProxySendsWhere(host);
        ProxyHttp = headers.Count > 0 && u?.Scheme == Uri.UriSchemeHttp;
        var bad = (RequestHeaders ?? string.Empty).Split('\n').Select((l, i) => (Line: l.Trim(), Number: i + 1))
            .Where(x => x.Line.Length > 0 && !x.Line.StartsWith('#') && ServerHeaders.Parse(x.Line).Count == 0)
            .Select(x => x.Number).FirstOrDefault();
        ProxyLineWarning = bad > 0 ? Strings.ProxyLineNotSent(bad.ToString(System.Globalization.CultureInfo.CurrentCulture)) : string.Empty;

        var savedUrl = (string?)_opened["baseUrl"] ?? string.Empty;
        var moved = SavedHeaders.Trim().Length > 0 && url.Length > 0 && !ServerScan.SameAddress(url, savedUrl);
        if (moved != ProxyMovedVisible)
        {
            // Another address: the default choice applies at once, so the preview shows what WILL leave — nothing. Back
            // on the saved address: its credentials again. A Test of the other address says nothing of this one.
            // What the form held before is given back when the address returns — an edit not saved yet included.
            if (moved) _proxyBeforeMove = RequestHeaders ?? string.Empty;
            ProxyMovedVisible = moved;
            ProxyMovedChoice  = "none";
            ProxyHopsVisible  = false;
            LoadProxy(moved ? string.Empty : _proxyBeforeMove);
            return;   // LoadProxy drew the rest
        }
        if (moved)
        {
            var oldHost = Uri.TryCreate(savedUrl, UriKind.Absolute, out var o) ? o.Host : savedUrl;
            ProxyMovedTitle = Strings.ProxyMovedTitle(oldHost);
            ProxyMovedBody  = Strings.ProxyMovedBody(oldHost, host);
            ProxyMovedFor   = Strings.ProxyMovedFor(host);
            ProxyMovedWas   = Strings.ProxyMovedWas(savedUrl);
        }
    }

    /// <summary>A header's value as the preview shows it: Cloudflare's Client ID is no secret, everything else is.</summary>
    private static string Masked(string name, string value) =>
        name.Equals(ProxyAccess.CfId, StringComparison.OrdinalIgnoreCase) ? value
        : name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.IndexOf(' ') is var sp and > 0
            ? value[..sp] + " ••••••••"
            : "••••••••";

    /// <summary>The Test's hops, drawn under the button.</summary>
    private void ShowProxyReport(ProxyReport report)
    {
        ProxyHops.Clear();
        var dark = IsDarkTheme;
        var hc   = HighContrastPalette();
        foreach (var hop in report.Hops)
            ProxyHops.Add(ProxyHopItem.Of(hop, hc?.Text ?? (dark ? "#7FD1A0" : "#1E7B3C"), hc?.Text ?? (dark ? "#F48771" : "#B3261E"),
                                          SubtleForeground, EditPanelBg));
        ProxyHopsVisible = ProxyHops.Count > 0;
    }

    /// <summary>The setting's own headers at the save: the old proxy's kept only when asked.</summary>
    private bool KeepProxyHeadersOnMove => ProxyMovedVisible && ProxyMovedChoice == "keep";

    private static string Code(ProxyMethod m) => m switch
    {
        ProxyMethod.Basic => "basic", ProxyMethod.Bearer => "bearer", ProxyMethod.Cloudflare => "cloudflare",
        ProxyMethod.Custom => "custom", _ => "none",
    };

    private static ProxyMethod MethodOf(string code) => code switch
    {
        "basic" => ProxyMethod.Basic, "bearer" => ProxyMethod.Bearer, "cloudflare" => ProxyMethod.Cloudflare,
        "custom" => ProxyMethod.Custom, _ => ProxyMethod.None,
    };
}
