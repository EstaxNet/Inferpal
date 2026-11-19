using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using Inferpal.Localization;

namespace Inferpal.ToolWindow;

/// <summary>
/// The cards of a run on a copy of the repository (<c>/run-copy</c>), one item of role <c>copy</c> drawn as its start, its
/// review or what the apply did (<see cref="CopyKind"/>) — the same texts as VS Code's, from the Core's <see cref="CopyCards"/>.
/// </summary>
/// <remarks>Visual Studio draws a card's sentences as plain text: their identifiers lose the backticks VS Code draws as
/// code (<see cref="Plain"/>); a file's path and its lines keep the code font.</remarks>
internal partial class ChatMessageItem
{
    /// <summary>"start", "review" or "applied": which of the three cards this item is.</summary>
    [DataMember] public string CopyKind { get; private set; } = string.Empty;
    [DataMember] public bool IsCopyStart   => CopyKind == "start";
    [DataMember] public bool IsCopyReview  => CopyKind == "review";
    [DataMember] public bool IsCopyApplied => CopyKind == "applied";

    [DataMember] public ObservableCollection<CopyRowItem> CopyRows { get; } = [];
    [DataMember] public string CopyUncommitted     { get; private set; } = string.Empty;
    [DataMember] public bool   HasCopyUncommitted  { get; private set; }
    [DataMember] public ObservableCollection<string> CopyUncommittedFiles { get; } = [];
    [DataMember] public string CopyUncommittedHint { get; private set; } = string.Empty;
    [DataMember] public string CopyRules           { get; private set; } = string.Empty;
    [DataMember] public string BtnCopyGo           { get; private set; } = string.Empty;
    [DataMember] public string BtnCopyMine         { get; private set; } = string.Empty;
    [DataMember] public string BtnCopyCancel       { get; private set; } = string.Empty;

    [DataMember] public ObservableCollection<CopyFileItem> CopyFiles { get; } = [];
    [DataMember] public string CopyNothing     { get; private set; } = string.Empty;
    [DataMember] public bool   HasCopyNothing  { get; private set; }
    [DataMember] public string CopyLeftOut     { get; private set; } = string.Empty;
    [DataMember] public bool   HasCopyLeftOut  { get; private set; }
    private string _copyPartial = string.Empty;
    [DataMember] public string CopyPartial { get => _copyPartial; private set { SetProperty(ref _copyPartial, value); HasCopyPartial = value.Length > 0; } }
    private bool _hasCopyPartial;
    [DataMember] public bool   HasCopyPartial { get => _hasCopyPartial; private set => SetProperty(ref _hasCopyPartial, value); }
    private string _btnCopyApply = string.Empty;
    [DataMember] public string BtnCopyApply { get => _btnCopyApply; private set => SetProperty(ref _btnCopyApply, value); }
    private bool _canCopyApply;
    [DataMember] public bool   CanCopyApply { get => _canCopyApply; private set => SetProperty(ref _canCopyApply, value); }
    [DataMember] public string BtnCopyDiscard  { get; private set; } = string.Empty;

    [DataMember] public string CopyUndo        { get; private set; } = string.Empty;
    [DataMember] public bool   HasCopyUndo     { get; private set; }
    [DataMember] public ObservableCollection<CopyLeftGroupItem> CopyGroups { get; } = [];
    [DataMember] public string BtnCopyKeep     { get; private set; } = string.Empty;

    private bool _copyCanAct = true;
    /// <summary>False while one of the card's gestures runs: one at a time, the card drawn again from its answer.</summary>
    [DataMember] public bool CopyCanAct { get => _copyCanAct; internal set => SetProperty(ref _copyCanAct, value); }

    [DataMember] public AsyncCommand? CopyGoCommand      { get; private set; }
    [DataMember] public AsyncCommand? CopyMineCommand    { get; private set; }
    [DataMember] public AsyncCommand? CopyCancelCommand  { get; private set; }
    [DataMember] public AsyncCommand? CopyApplyCommand   { get; private set; }
    [DataMember] public AsyncCommand? CopyDiscardCommand { get; private set; }
    [DataMember] public AsyncCommand? CopyKeepCommand    { get; private set; }

    // ── The copy a closing left behind, in the notice of a start (its item's role is "away") ──
    private bool _hasAwayCopy;
    /// <summary>The notice names a copy left behind; off once reviewed or discarded from it.</summary>
    [DataMember] public bool HasAwayCopy { get => _hasAwayCopy; private set => SetProperty(ref _hasAwayCopy, value); }
    [DataMember] public string AwayCopyText       { get; private set; } = string.Empty;
    [DataMember] public string AwayCopyNote       { get; private set; } = string.Empty;
    [DataMember] public string BtnAwayCopyReview  { get; private set; } = string.Empty;
    [DataMember] public string BtnAwayCopyDiscard { get; private set; } = string.Empty;
    [DataMember] public AsyncCommand? AwayCopyReviewCommand  { get; private set; }
    [DataMember] public AsyncCommand? AwayCopyDiscardCommand { get; private set; }

    private CopyReviewTexts? _copyTexts;

    /// <summary>The copy a review card shows: its gestures act on that copy, whatever workspace is open by then.</summary>
    internal Services.Execution.CopyRun? CopyRun { get; private set; }

    /// <summary>A sentence of the Core as Visual Studio draws it: its identifiers without the backticks.</summary>
    internal static string Plain(string text) => text.Replace("`", string.Empty);

    /// <summary>A gesture of the card: the card's actions off until the window draws its answer.</summary>
    private AsyncCommand CopyGesture(Action act) => new((_, _) =>
    {
        if (!CopyCanAct) return Task.CompletedTask;
        CopyCanAct = false;
        act();
        return Task.CompletedTask;
    });

    /// <summary>The start card: what the copy starts from, and what does not go with it.</summary>
    internal static ChatMessageItem CopyStartMsg(CopyStartCardModel card, Action go, Action mine, Action cancel)
    {
        var item = new ChatMessageItem
        {
            Role = "copy", CopyKind = "start", CardTitle = card.Title, CardSubject = card.Lead,
            CopyUncommitted = Plain(card.Uncommitted ?? string.Empty), HasCopyUncommitted = card.Uncommitted is not null,
            CopyUncommittedHint = card.UncommittedHint ?? string.Empty, CopyRules = Plain(card.Rules),
            BtnCopyGo = card.Go, BtnCopyMine = card.Mine, BtnCopyCancel = card.Cancel,
        };
        foreach (var row in card.Rows) item.CopyRows.Add(new CopyRowItem(row.Label, Plain(row.Value)));
        foreach (var file in card.UncommittedFiles) item.CopyUncommittedFiles.Add(Plain(file));
        item.CopyGoCommand = item.CopyGesture(go);
        item.CopyMineCommand = item.CopyGesture(mine);
        item.CopyCancelCommand = item.CopyGesture(cancel);
        return item;
    }

    /// <summary>
    /// The review card of <paramref name="copy"/>: each change of each file kept or dropped (<paramref name="dropped"/>: the
    /// changes dropped on the card before, by <see cref="CopyCards.HunkKey"/>), then applied — <paramref name="apply"/>
    /// receives the kept changes by path — or the copy discarded.
    /// </summary>
    internal static ChatMessageItem CopyReviewMsg(Services.Execution.CopyRun copy, Services.Execution.CopyReview review,
                                                  IReadOnlySet<string>? dropped,
                                                  Action<IReadOnlyDictionary<string, IReadOnlyCollection<int>>> apply, Action discard)
    {
        var texts = CopyCards.Review(review);
        var item = new ChatMessageItem
        {
            Role = "copy", CopyKind = "review", CardTitle = texts.Title, _copyTexts = texts, CopyRun = copy,
            CopyNothing = texts.Nothing ?? string.Empty, HasCopyNothing = texts.Nothing is not null,
            CopyLeftOut = texts.LeftOut ?? string.Empty, HasCopyLeftOut = texts.LeftOut is not null,
            BtnCopyDiscard = texts.Discard,
        };
        for (var at = 0; at < review.Files.Count; at++)
        {
            var file = review.Files[at];
            item.CopyFiles.Add(new CopyFileItem(file, CopyCards.File(file), texts, expanded: at < 2,
                                                dropped ?? new HashSet<string>(), item.RefreshCopyCounts));
        }
        item.CopyApplyCommand = item.CopyGesture(() => apply(item.KeptChanges()));
        item.CopyDiscardCommand = item.CopyGesture(discard);
        item.RefreshCopyCounts();
        return item;
    }

    /// <summary>The changes the person keeps, by path; a file with none kept is not named.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyCollection<int>> KeptChanges() =>
        CopyFiles.Select(f => (f.Path, Kept: f.Hunks.Where(h => h.IsKept).Select(h => h.Index).ToList()))
                 .Where(f => f.Kept.Count > 0)
                 .ToDictionary(f => f.Path, f => (IReadOnlyCollection<int>)f.Kept, StringComparer.Ordinal);

    /// <summary>The changes the person dropped, by what they are: what a review read again keeps dropped. VM context.</summary>
    internal IReadOnlySet<string> DroppedKeys() =>
        CopyFiles.SelectMany(f => f.Hunks).Where(h => !h.IsKept).Select(h => h.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The counted texts for the changes kept now: the card's line, the apply button, the partial note.</summary>
    private void RefreshCopyCounts()
    {
        if (_copyTexts is not { } texts) return;
        var kept = CopyFiles.Sum(f => f.Hunks.Count(h => h.IsKept));
        CardSubject = texts.Counts[kept];
        RaiseNotifyPropertyChangedEvent(nameof(CardSubject));
        BtnCopyApply = texts.Apply[kept];
        CanCopyApply = kept > 0;
        CopyPartial = texts.Partial[kept];
    }

    /// <summary>The card after an apply: what was written, what was not and why, and the copy discarded or kept for now.</summary>
    internal static ChatMessageItem CopyAppliedMsg(CopyAppliedCardModel card, Action<CopyNotAppliedFile> compare,
                                                   Action<CopyNotAppliedFile> merge, Action discard, Action keep)
    {
        var item = new ChatMessageItem
        {
            Role = "copy", CopyKind = "applied", CardTitle = card.Title,
            CopyUndo = Plain(card.Undo ?? string.Empty), HasCopyUndo = card.Undo is not null,
            BtnCopyDiscard = card.Discard, BtnCopyKeep = card.KeepForNow,
        };
        foreach (var group in card.NotApplied)
            item.CopyGroups.Add(new CopyLeftGroupItem(group, card.Compare, card.Merge, compare, merge));
        item.CopyDiscardCommand = item.CopyGesture(discard);
        item.CopyKeepCommand = item.CopyGesture(keep);
        return item;
    }
}

/// <summary>One row of the start card.</summary>
[DataContract]
internal sealed class CopyRowItem(string label, string value)
{
    [DataMember] public string Label { get; } = label;
    [DataMember] public string Value { get; } = value;
}

/// <summary>One file of the review card: its changes, each kept or dropped; the files after the first two folded.</summary>
[DataContract]
internal sealed class CopyFileItem : NotifyPropertyChangedObject
{
    private readonly CopyReviewFileTexts _texts;
    private readonly ReviewTexts _labels;

    /// <summary>The labels a file's changes share.</summary>
    private sealed record ReviewTexts(string Show, string Hide);

    public CopyFileItem(Services.Execution.CopyReviewFile file, CopyReviewFileTexts texts, CopyReviewTexts review, bool expanded,
                        IReadOnlySet<string> dropped, Action changed)
    {
        _texts = texts;
        _labels = new ReviewTexts(review.Show, review.Hide);
        Path = file.Path;
        Tag = texts.Tag ?? string.Empty;
        HasTag = texts.Tag is not null;
        IsFolded = !expanded;
        _isExpanded = expanded;
        ToggleLabel = expanded ? review.Hide : review.Show;
        for (var n = 0; n < file.Hunks.Count; n++)
            Hunks.Add(new CopyHunkItem(file.Path, file.Hunks[n], texts.HunkLabels[n], texts.HunkKeys[n], review,
                                       !dropped.Contains(texts.HunkKeys[n]), () => { Refresh(); changed(); }));
        ToggleCommand = new AsyncCommand((_, _) =>
        {
            IsExpanded = !IsExpanded;
            ToggleLabel = IsExpanded ? _labels.Hide : _labels.Show;
            return Task.CompletedTask;
        });
        Refresh();
    }

    private void Refresh()
    {
        var kept = Hunks.Count(h => h.IsKept);
        KeptLabel = _texts.Kept[kept];
        IsNoneKept = kept == 0;
    }

    [DataMember] public string Path    { get; }
    [DataMember] public string Tag     { get; }
    [DataMember] public bool   HasTag  { get; }
    /// <summary>Drawn folded at first (after the first two files): its Show button stays.</summary>
    [DataMember] public bool   IsFolded { get; }
    private bool _isExpanded;
    [DataMember] public bool   IsExpanded { get => _isExpanded; private set => SetProperty(ref _isExpanded, value); }
    private string _toggleLabel = string.Empty;
    [DataMember] public string ToggleLabel { get => _toggleLabel; private set => SetProperty(ref _toggleLabel, value); }
    private string _keptLabel = string.Empty;
    [DataMember] public string KeptLabel { get => _keptLabel; private set => SetProperty(ref _keptLabel, value); }
    private bool _isNoneKept;
    [DataMember] public bool   IsNoneKept { get => _isNoneKept; private set => SetProperty(ref _isNoneKept, value); }
    [DataMember] public ObservableCollection<CopyHunkItem> Hunks { get; } = [];
    [DataMember] public AsyncCommand ToggleCommand { get; }
}

/// <summary>One change of a file on the review card: its lines, kept or dropped.</summary>
[DataContract]
internal sealed class CopyHunkItem : NotifyPropertyChangedObject
{
    private readonly string _drop, _dropped;

    public CopyHunkItem(string path, Services.CodeActions.DiffHunk hunk, string label, string key, CopyReviewTexts texts, bool kept,
                        Action changed)
    {
        Index = hunk.Index;
        Key = key;
        Label = label;
        BtnKeep = texts.Keep;
        _drop = texts.Drop;
        _dropped = texts.Dropped;
        // A panel has no automation peer: each button names the change it acts on, its pressed state is IsKept's.
        KeepName = $"{texts.Keep}: {path} · {label}";
        DropName = $"{texts.Drop}: {path} · {label}";
        foreach (var line in hunk.OldLines) Lines.Add(new DiffLine { Prefix = "-", Text = line });
        foreach (var line in hunk.NewLines) Lines.Add(new DiffLine { Prefix = "+", Text = line });
        _isKept = kept;
        BtnDrop = kept ? _drop : _dropped;
        KeepCommand = new AsyncCommand((_, _) => { Set(true); changed(); return Task.CompletedTask; });
        DropCommand = new AsyncCommand((_, _) => { Set(false); changed(); return Task.CompletedTask; });
    }

    private void Set(bool kept)
    {
        IsKept = kept;
        BtnDrop = kept ? _drop : _dropped;
    }

    internal int Index { get; }
    /// <summary>The change by what it is (<see cref="CopyCards.HunkKey"/>).</summary>
    internal string Key { get; }
    [DataMember] public string Label    { get; }
    [DataMember] public string BtnKeep  { get; }
    [DataMember] public string KeepName { get; }
    [DataMember] public string DropName { get; }
    private string _btnDrop = string.Empty;
    [DataMember] public string BtnDrop { get => _btnDrop; private set => SetProperty(ref _btnDrop, value); }
    private bool _isKept;
    [DataMember] public bool IsKept { get => _isKept; private set => SetProperty(ref _isKept, value); }
    [DataMember] public ObservableCollection<DiffLine> Lines { get; } = [];
    [DataMember] public AsyncCommand KeepCommand { get; }
    [DataMember] public AsyncCommand DropCommand { get; }
}

/// <summary>The kept changes not applied for one reason, on the card after an apply.</summary>
[DataContract]
internal sealed class CopyLeftGroupItem
{
    public CopyLeftGroupItem(CopyNotAppliedGroup group, string compare, string merge, Action<CopyNotAppliedFile> onCompare,
                             Action<CopyNotAppliedFile> onMerge)
    {
        Title = group.Title;
        Note = ChatMessageItem.Plain(group.Note ?? string.Empty);
        HasNote = group.Note is not null;
        foreach (var file in group.Files) Files.Add(new CopyLeftFileItem(file, compare, merge, onCompare, onMerge));
    }

    [DataMember] public string Title   { get; }
    [DataMember] public string Note    { get; }
    [DataMember] public bool   HasNote { get; }
    [DataMember] public ObservableCollection<CopyLeftFileItem> Files { get; } = [];
}

/// <summary>A file whose kept changes were not applied: where they are, and — the person's own version the reason — the
/// comparison and the question to merge it.</summary>
[DataContract]
internal sealed class CopyLeftFileItem(CopyNotAppliedFile file, string compare, string merge, Action<CopyNotAppliedFile> onCompare,
                                       Action<CopyNotAppliedFile> onMerge)
{
    [DataMember] public string Path       { get; } = file.Path;
    [DataMember] public string Lines      { get; } = file.Lines;
    [DataMember] public bool   CanMerge   { get; } = file.CanMerge;
    [DataMember] public string BtnCompare { get; } = compare;
    [DataMember] public string BtnMerge   { get; } = merge;
    [DataMember] public AsyncCommand CompareCommand { get; } = new((_, _) => { onCompare(file); return Task.CompletedTask; });
    [DataMember] public AsyncCommand MergeCommand   { get; } = new((_, _) => { onMerge(file); return Task.CompletedTask; });
}
