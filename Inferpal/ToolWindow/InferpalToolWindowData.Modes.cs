using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Modes found for the workspace (the composer's fourth segment)

    // One selector: a mode found here (`.inferpal/modes/`, a Copilot agent, a Roo mode) holds the conversation instead of
    // Chat, Agent and Plan — its instructions, tools, model and effort — until the person picks another one.

    /// <summary>The mode the conversation is held in, as read at the last question or pick; <c>null</c> for Chat, Agent
    /// and Plan.</summary>
    private ChatMode? _heldMode;

    /// <summary>The mode whose missing models this conversation was already told about: said once, not at every turn.</summary>
    private string? _modeModelNoticeSaid;

    /// <summary>How many modes the last reading found here; <c>null</c> before the first reading, which never says
    /// "no mode" for a workspace not read yet.</summary>
    private int? _foundModeCount;

    private bool   _isModeMenuOpen, _hasFoundModes, _hasNoFoundModes;
    private string _modesSegmentLabel = string.Empty, _modesSegmentTip = string.Empty,
                   _modesHeader       = string.Empty, _modesFooter     = string.Empty,
                   _modesNone         = string.Empty, _modesWriteOne   = string.Empty;

    /// <summary>Chat, Agent and Plan, at the top of the list.</summary>
    [DataMember] public ObservableCollection<ModeMenuItem> OwnModeItems   { get; } = [];
    /// <summary>The modes found here, under their header.</summary>
    [DataMember] public ObservableCollection<ModeMenuItem> FoundModeItems { get; } = [];

    [DataMember] public bool   IsModeMenuOpen    { get => _isModeMenuOpen;    set => SetProperty(ref _isModeMenuOpen,    value); }
    [DataMember] public bool   HasFoundModes     { get => _hasFoundModes;     set => SetProperty(ref _hasFoundModes,     value); }
    /// <summary>No mode here: the list says where one goes instead of an empty section.</summary>
    [DataMember] public bool   HasNoFoundModes   { get => _hasNoFoundModes;   set => SetProperty(ref _hasNoFoundModes,   value); }
    [DataMember] public string ModesNone         { get => _modesNone;         set => SetProperty(ref _modesNone,         value); }
    [DataMember] public string ModesWriteOne     { get => _modesWriteOne;     set => SetProperty(ref _modesWriteOne,     value); }
    /// <summary>The held mode's name, or "Modes".</summary>
    [DataMember] public string ModesSegmentLabel { get => _modesSegmentLabel; set => SetProperty(ref _modesSegmentLabel, value); }
    [DataMember] public string ModesSegmentTip   { get => _modesSegmentTip;   set => SetProperty(ref _modesSegmentTip,   value); }
    [DataMember] public string ModesHeader       { get => _modesHeader;       set => SetProperty(ref _modesHeader,       value); }
    [DataMember] public string ModesFooter       { get => _modesFooter;       set => SetProperty(ref _modesFooter,       value); }

    /// <summary>Opens the list (read again as it opens), or closes it.</summary>
    [DataMember] public AsyncCommand ToggleModeMenuCommand { get; private set; } = null!;
    /// <summary>The "Modes" section of this version's configuration page, in the browser; not opened, its address is said.</summary>
    [DataMember] public AsyncCommand WriteModeCommand { get; private set; } = null!;

    private async Task OpenModesDocumentationAsync(object? _, CancellationToken __)
    {
        await RunOnVMContextAsync(() => IsModeMenuOpen = false);
        var docs = DocsLinks.Modes;
        if (!WebLink.Open(docs)) await ShowInfoAsync(Strings.HelpDocumentation(docs));
    }

    /// <summary>The modes, read where <c>/modes</c> and the system prompt read <c>.inferpal/</c>.</summary>
    private ChatModeCatalog ModeCatalog() => ChatModes.For(_config, FindProjectRoot());

    private async Task ToggleModeMenuAsync(object? _, CancellationToken ct)
    {
        if (IsModeMenuOpen)
        {
            await RunOnVMContextAsync(() => IsModeMenuOpen = false);
            return;
        }
        await RunOnVMContextAsync(() =>
        {
            CloseMenus();
            IsAttachMenuOpen = false;
            IsModeMenuOpen   = true;
        });
        // Open at once, then read again: a mode file added or edited since shows, with the model each answers with now
        // — a server slow to list its models never holds the list back.
        await RefreshModesAsync(models: true, ct);
    }

    /// <summary>Reads the modes found here and fills the list and the segment; <paramref name="models"/> asks the server
    /// which model each answers with — one listing for all.</summary>
    private async Task RefreshModesAsync(bool models, CancellationToken ct)
    {
        var catalog = ModeCatalog();
        IReadOnlyList<string> chat = [];
        if (models && catalog.Modes.Any(m => m.Models.Count > 0))
        {
            try
            {
                chat = (await _client.ListModelsByKindAsync(ct)).Chat;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Unlisted, each mode shows the conversation's model; the turn says what the server answers.
                Diagnostics.Swallow("Modes.ListModels", ex);
            }
        }

        await RunOnVMContextAsync(() =>
        {
            if (_heldMode is { } held && catalog.Find(held.Name) is { } fresh) _heldMode = fresh;
            var p = ThemePalette.For(_isDark, _isHighContrast);
            OwnModeItems.Clear();
            foreach (var (mode, label, tip) in new[]
                     {
                         ("chat",  Strings.ModeChat,  Strings.ModeChatTip),
                         ("agent", Strings.ModeAgent, Strings.ModeAgentTip),
                         ("plan",  Strings.ModePlan,  Strings.ModePlanTip),
                     })
            {
                OwnModeItems.Add(new ModeMenuItem(label, tip, string.Empty, CurrentMode() == mode,
                                                  p.Text, p.SuggestionSubtleText, p.HoverBg, _ => PickOwnModeAsync(mode)));
            }
            FoundModeItems.Clear();
            foreach (var m in catalog.Modes)
            {
                var name = m.Name;
                FoundModeItems.Add(new ModeMenuItem(name, m.Description, ModePresenter.Summary(m, m.PickModel(chat)),
                                                    _heldMode?.Name == name, p.Text, p.SuggestionSubtleText, p.HoverBg,
                                                    tok => HoldModeAsync(name, tok)));
            }
            _foundModeCount = catalog.Modes.Count;
            HasFoundModes   = _foundModeCount > 0;
            RefreshModesSegment();
        });
    }

    /// <summary>The segment follows the workspace: read again when the window loads and when the root is pinned.</summary>
    private async Task RefreshModesQuietlyAsync()
    {
        try
        {
            await RefreshModesAsync(models: true, CancellationToken.None);
        }
        catch (Exception ex) { Diagnostics.Swallow("Modes.Refresh", ex); }
    }

    /// <summary>The fourth segment and the list's own words, from what is held and found now. VM context only; called by
    /// <c>ApplyLabels</c> too, so the list opened before its first reading is never wordless. ⚠ The segment is always
    /// shown, modes found or not: hidden without one, nothing in the window said modes exist — the list says where one
    /// goes.</summary>
    private void RefreshModesSegment()
    {
        ModesHeader       = Strings.ModesFoundHeader;
        ModesFooter       = Strings.ModesFooter;
        ModesNone         = ModePresenter.None;
        ModesWriteOne     = Strings.ModesWriteOne;
        // "No mode found" only once read, and never under a held mode whose file has gone: the segment still names it.
        HasNoFoundModes   = _foundModeCount == 0 && _heldMode is null;
        ModesSegmentLabel = _heldMode?.Name ?? Strings.ModesSegment;
        // The held mode's row says the model it answers with, as the server listed it at the last reading.
        ModesSegmentTip   = _heldMode is { } held
            ? $"{held.Name} — {FoundModeItems.FirstOrDefault(i => i.Name == held.Name)?.Summary ?? ModePresenter.Summary(held, null)}"
            : ModePresenter.SegmentTip(_foundModeCount);
    }

    /// <summary>Chat, Agent or Plan, picked in the list: the composer's switch, which leaves a held mode.</summary>
    private async Task PickOwnModeAsync(string mode)
    {
        await RunOnVMContextAsync(() => IsModeMenuOpen = false);
        await SetModeAsync(mode);
    }

    /// <summary>A mode found here, picked in the list: it holds the conversation (it leaves Plan) and the thread says where
    /// it starts.</summary>
    private async Task HoldModeAsync(string name, CancellationToken ct)
    {
        // Already held: no second line, and the missing-model notice is not said again.
        if (_heldMode?.Name == name)
        {
            await RunOnVMContextAsync(() => IsModeMenuOpen = false);
            return;
        }
        // Read again: gone since the list was read, it is said, never held.
        var mode = ModeCatalog().Find(name);
        if (mode is null)
        {
            await RunOnVMContextAsync(() => IsModeMenuOpen = false);
            await ShowInfoAsync(Strings.ModeUnknown(name));
            return;
        }
        await RunOnVMContextAsync(() =>
        {
            IsModeMenuOpen       = false;
            _heldMode            = mode;
            _modeModelNoticeSaid = null;
            IsPlanMode           = false;   // one selector: a mode leaves Plan
            ChatMode             = CurrentMode();
            RefreshModesSegment();
            RefreshSystemPrompt();
            InsertThemed(ChatMessageItem.ModeLineMsg(ModePresenter.HeldLine(mode)));
            ScrollToBottom();
        });
    }

    /// <summary>Leaves the held mode for Chat, Agent or Plan; whether one was held (the system prompt then changes).
    /// VM context only.</summary>
    private bool LeaveHeldMode()
    {
        if (_heldMode is null) return false;
        _heldMode = null;
        RefreshModesSegment();
        return true;
    }

    /// <summary>The held mode for the next question, read from its file again — an edit applies to the next question. A
    /// mode whose file is gone leaves the conversation, and the thread says so. VM context only.</summary>
    private void RereadHeldMode()
    {
        if (_heldMode is not { } held) return;
        _heldMode = ModeCatalog().Find(held.Name);
        if (_heldMode is not null) return;
        InsertThemed(ChatMessageItem.NoticeMsg(Strings.ModeGone(held.Name)));
        ChatMode = CurrentMode();
        RefreshModesSegment();
    }

    /// <summary>The mode a restored conversation was held in, held again — or none, for one held in Chat, Agent or Plan.
    /// A mode whose file is gone is said (after the restored thread), and the conversation comes back without it. VM
    /// context only, before the system prompt is rebuilt.</summary>
    private string? HoldRestoredMode(string? name)
    {
        _heldMode            = name is null ? null : ModeCatalog().Find(name);
        _modeModelNoticeSaid = null;
        if (_heldMode is not null) IsPlanMode = false;
        ChatMode = CurrentMode();
        RefreshModesSegment();
        return name is not null && _heldMode is null ? Strings.ModeGone(name) : null;
    }

    #endregion
}
