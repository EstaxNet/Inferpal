using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// Data context of the "Review a commit" dialog (<see cref="ReviewCommitDialogControl"/>): the recent commits of the
/// current branch, filtered as the person types — a hash that starts with the text, a subject that holds it —, and the
/// typed text itself as a line of its own when it can name a branch, tag or range
/// (<see cref="Services.Commands.ReviewCommits"/>, the rule of VS Code's picker). Review completes <see cref="Chosen"/>
/// with what <c>/check</c> reviews.
/// </summary>
[DataContract]
internal sealed class ReviewCommitDialogData : NotifyPropertyChangedObject
{
    private readonly TaskCompletionSource<string?> _chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<Services.Commands.CommitEntry> _commits;
    private readonly string _selectedBackground;
    private readonly string _selectedLine;
    private string _filter = string.Empty;
    private ReviewCommitRow? _selected;
    private bool _hasNoMatch;
    private bool _canReview;
    // A click (a row's command) and a keystroke (the Filter setter) arrive on their own threads.
    private readonly object _gate = new();

    [DataMember] public string Intro       { get; }
    [DataMember] public string FilterLabel { get; }
    [DataMember] public string Footer      { get; }
    [DataMember] public string NoMatch     { get; }
    [DataMember] public string Failure     { get; }
    [DataMember] public bool   HasFailure  { get; }
    /// <summary>The list was read: its footer says what it holds.</summary>
    [DataMember] public bool   HasCommits  { get; }
    [DataMember] public string BtnReview   { get; }
    [DataMember] public string BtnCancel   { get; }

    // Theme colours as plain strings (no theme propagation across the Remote UI boundary), as the approval dialog.
    [DataMember] public string ThemeText       { get; }
    [DataMember] public string ThemeSubtleText { get; }
    [DataMember] public string ThemeBorder     { get; }
    /// <summary>The dialog paints its own background: the palette's colours are chosen against it — white text on a
    /// light high-contrast dialog would not read.</summary>
    [DataMember] public string ThemeWindowBg   { get; }

    [DataMember] public ObservableCollection<ReviewCommitRow> Rows { get; } = [];
    [DataMember] public bool HasNoMatch { get => _hasNoMatch; private set => SetProperty(ref _hasNoMatch, value); }
    [DataMember] public bool CanReview  { get => _canReview;  private set => SetProperty(ref _canReview, value); }

    /// <summary>The typed text: it filters the list, and is a line of its own when it can name a revision.</summary>
    [DataMember]
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty)) Refresh();
        }
    }

    [DataMember] public AsyncCommand ReviewCommand { get; }
    [DataMember] public AsyncCommand CancelCommand { get; }
    /// <summary>↓ and ↑ in the search box: the next or previous line — the keyboard picks a commit found behind the
    /// typed text without leaving the box, and Enter reviews it.</summary>
    [DataMember] public AsyncCommand NextCommand     { get; }
    [DataMember] public AsyncCommand PreviousCommand { get; }

    /// <summary>What <c>/check</c> reviews — a commit's full hash or the typed text —; <c>null</c> when cancelled. Never
    /// completes when the dialog is dismissed otherwise: the caller then reviews nothing.</summary>
    public Task<string?> Chosen => _chosen.Task;

    /// <param name="commits">The recent commits, newest first (<see cref="Services.Commands.ReviewCommits.ReadAsync"/>).</param>
    /// <param name="failure">Why there are none — git's refusal, no workspace —; empty when the list was read.</param>
    public ReviewCommitDialogData(IReadOnlyList<Services.Commands.CommitEntry> commits, string failure)
    {
        _commits    = commits;
        Intro       = Strings.ReviewCommitIntro;
        FilterLabel = Strings.ReviewCommitFilter;
        Footer      = Strings.ReviewCommitFooter(Services.Commands.ReviewCommits.Shown, Services.Commands.ReviewCommits.Searched);
        NoMatch     = Strings.ReviewCommitNoMatch(Services.Commands.ReviewCommits.Searched);
        Failure     = failure;
        HasFailure  = failure.Length > 0;
        HasCommits  = !HasFailure;
        BtnReview   = Strings.ReviewCommitReview;
        BtnCancel   = Strings.InputCancel;

        var palette = ThemePalette.For(VsThemeDetector.CurrentIsDark, VsThemeDetector.HighContrastOn());
        ThemeText           = palette.Text;
        ThemeSubtleText     = palette.SubtleText;
        ThemeBorder         = palette.Border;
        ThemeWindowBg       = palette.WindowBg;
        _selectedBackground = palette.HoverBg;
        _selectedLine       = palette.AccentLine;

        ReviewCommand = new AsyncCommand((_, _) =>
        {
            lock (_gate)
                if (_selected is { } row) _chosen.TrySetResult(row.Revision);
            return Task.CompletedTask;
        });
        CancelCommand = new AsyncCommand((_, _) =>
        {
            _chosen.TrySetResult(null);
            return Task.CompletedTask;
        });
        NextCommand     = new AsyncCommand((_, _) => { Move(+1); return Task.CompletedTask; });
        PreviousCommand = new AsyncCommand((_, _) => { Move(-1); return Task.CompletedTask; });
        Refresh();
    }

    /// <summary>The lines for the typed text — the text itself first when it can name a revision, then the commits it
    /// finds —, the first one selected: Enter reviews what is drawn highlighted, never a line behind the text. None when
    /// git refused: in a folder that is no repository, a typed branch reviews nothing either.</summary>
    private void Refresh()
    {
        lock (_gate)
        {
            Rows.Clear();
            if (!HasFailure)
            {
                if (Services.Commands.ReviewCommits.TypedRevision(_filter) is { } typed)
                    Rows.Add(new ReviewCommitRow(typed, Strings.ReviewCommitTyped, string.Empty, typed, _selectedBackground, _selectedLine, Select));
                var now = DateTime.UtcNow;
                foreach (var c in Services.Commands.ReviewCommits.Filter(_commits, _filter))
                    Rows.Add(new ReviewCommitRow(c.ShortHash, c.Subject, $"{c.Author} · {SessionManager.FormatAge(c.When.UtcDateTime, now)}",
                                                 c.Hash, _selectedBackground, _selectedLine, Select));
            }
            HasNoMatch = !HasFailure && Rows.Count == 0;
            SelectLocked(Rows.FirstOrDefault());
        }
    }

    /// <summary>A click on a line; one the list no longer shows — a keystroke rebuilt it meanwhile — selects nothing.</summary>
    private void Select(ReviewCommitRow row)
    {
        lock (_gate)
            if (Rows.Contains(row)) SelectLocked(row);
    }

    /// <summary>The line after or before the selected one, held at the ends.</summary>
    private void Move(int step)
    {
        lock (_gate)
        {
            if (Rows.Count == 0) return;
            var at = _selected is null ? -1 : Rows.IndexOf(_selected);
            SelectLocked(Rows[Math.Clamp(at + step, 0, Rows.Count - 1)]);
        }
    }

    private void SelectLocked(ReviewCommitRow? row)
    {
        _selected = row;
        foreach (var r in Rows) r.Mark(ReferenceEquals(r, row));
        CanReview = row is not null;
    }
}
