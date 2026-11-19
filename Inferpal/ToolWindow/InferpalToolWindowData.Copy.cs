using System.Diagnostics;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// A run on a copy of the repository (<c>/run-copy</c>) in Visual Studio: the start card, the question asked in the copy, the
/// review of what its run left — each change kept or dropped —, the kept ones applied to the person's files, the copy
/// discarded or kept for now. A copy is this window's, one per workspace: A → B → A finds A's copy again.
/// </summary>
/// <remarks>⚠ Every gesture of a card takes the turn, as a question does: applying opens a run of the history, which a turn
/// starting meanwhile would share — and close; a discard removes a worktree a turn would enter.</remarks>
internal partial class InferpalToolWindowData
{
    /// <summary>The copies this window holds, by the workspace each copies; one an editor that closed left is taken up by
    /// <c>/run-copy</c>.</summary>
    private readonly Dictionary<string, CopyRun> _copies = new(PathComparer.Default);

    /// <summary>The card of the copy on screen (start, review or applied): one at a time.</summary>
    private ChatMessageItem? _copyCard;

    private static string CopyKey(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>The copy held for the workspace open now; <c>null</c> when none.</summary>
    private CopyRun? HeldCopy =>
        _indexService.RootDir is { Length: > 0 } root && _copies.TryGetValue(CopyKey(root), out var held) && !held.Discarded ? held : null;

    private void Hold(CopyRun copy) => _copies[CopyKey(copy.Workspace)] = copy;

    /// <summary>A turn runs or is starting: a gesture of the copy waits for it.</summary>
    private bool TurnBusy => _sendStarting || IsLoading || _currentCts is not null;

    /// <summary>The last question of the conversation on screen — a note typed during a run is not one. VM context.</summary>
    private ChatMessageItem? LastQuestion()
    {
        for (var i = Messages.Count - 3; i >= 0; i--)
            if (Messages[i].Role == "user" && !Messages[i].IsNote) return Messages[i];
        return null;
    }

    /// <summary>
    /// <c>/run-copy &lt;question&gt;</c>: the start card, or why no copy can be made — nothing is made before the person
    /// answers. <c>/run-copy</c> alone: the review of the copy held, or of one an editor that closed left.
    /// </summary>
    private async Task HandleRunCopyCommandAsync(string question, CancellationToken ct)
    {
        PinWorkspaceRoot();
        var root = _indexService.RootDir;
        var held = await Services.Commands.RunCopyCommandHandler.HeldOrLeftAsync(HeldCopy, root, ct, ConversationStore.VisualStudioSlot);
        if (held is not null) Hold(held);
        if (question.Length == 0)
        {
            if (held is null) await ShowInfoAsync(Services.Commands.RunCopyCommandHandler.NoneOpen);
            else await ShowCopyReviewAsync(held, ct);
            return;
        }
        var start = await CopyRun.PrepareAsync(root, ct);
        if (start.Refusal is { } refusal)
        {
            await ShowInfoAsync(Strings.CopyNotMade(refusal));
            return;
        }
        var card = CopyCards.Start(start, held);
        await RunOnVMContextAsync(() => ShowCopyCard(ChatMessageItem.CopyStartMsg(card,
            go: () => _ = AnswerCopyStartAsync(question, toCopy: true),
            mine: () => _ = AnswerCopyStartAsync(question, toCopy: false),
            cancel: () => Post(() =>
            {
                RemoveCopyCard();
                Prompt = "/run-copy " + question;
            }))));
    }

    /// <summary>The card of the copy, in place of the one before. VM context.</summary>
    private void ShowCopyCard(ChatMessageItem card)
    {
        RemoveCopyCard();
        _copyCard = InsertThemed(card);
        ScrollToBottom();
    }

    private void RemoveCopyCard()
    {
        if (_copyCard is not null) Messages.Remove(_copyCard);
        _copyCard = null;
    }

    /// <summary>The card's buttons back, after a gesture that changed nothing. VM context.</summary>
    private void CopyCardBack()
    {
        if (_copyCard is not null) _copyCard.CopyCanAct = true;
    }

    /// <summary>
    /// A gesture of a copy card, with the turn taken for its length (<see cref="TurnBusy"/>): refused while a turn runs —
    /// said, the card's buttons back —, and what it throws said, never lost.
    /// </summary>
    private async Task CopyGestureAsync(Func<Task> act)
    {
        var claim = 0;
        await RunOnVMContextAsync(() =>
        {
            if (TurnBusy)
            {
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.CopyWhileBusy));
                CopyCardBack();
                return;
            }
            _sendStarting = true;
            claim = ++_sendClaim;
        });
        if (claim == 0) return;
        try { await act(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("Copy.Gesture", ex);
            await RunOnVMContextAsync(() =>
            {
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.MsgError(Diagnostics.RootMessage(ex))));
                CopyCardBack();
            });
        }
        finally
        {
            await RunOnVMContextAsync(() => { if (_sendClaim == claim) _sendStarting = false; });
        }
    }

    /// <summary>
    /// The start card's answer: the question on the copy (the one held, or a new one at the last commit), or in the person's
    /// files. ⚠ Refused while a turn runs or the server is out of reach, the card stays: taken away first, the question was
    /// lost with it.
    /// </summary>
    private Task AnswerCopyStartAsync(string question, bool toCopy) => CopyGestureAsync(async () =>
    {
        if (!_isBackendReachable)
        {
            await RunOnVMContextAsync(() =>
            {
                InsertThemed(ChatMessageItem.NoticeMsg(UnreachableText()));
                CopyCardBack();
            });
            return;
        }
        CopyRun? copy = null;
        if (toCopy)
        {
            var (made, refusal) = await Services.Commands.RunCopyCommandHandler.HeldOrNewAsync(HeldCopy, _indexService.RootDir,
                                                                                                    CancellationToken.None, ConversationStore.VisualStudioSlot);
            if (made is null)
            {
                await RunOnVMContextAsync(() =>
                {
                    InsertThemed(ChatMessageItem.NoticeMsg(refusal ?? Strings.CopyNotMade("git gave no reason")));
                    CopyCardBack();
                });
                return;
            }
            Hold(copy = made);
        }
        await RunOnVMContextAsync(RemoveCopyCard);
        await SendCoreAsync(question, null, attachments: [], CancellationToken.None, clearPrompt: false, typed: false, copy: copy);
    });

    /// <summary>
    /// The review card of what the run left in <paramref name="copy"/>; <c>null</c> for no copy. A read that fails is said on
    /// the card, whose discard still reaches the copy. The changes dropped on the card shown keep their choice — by what they
    /// are, never by their number.
    /// </summary>
    private async Task<ChatMessageItem?> CopyReviewCardAsync(CopyRun? copy, CancellationToken ct)
    {
        if (copy is null) return null;
        IReadOnlySet<string>? dropped = null;
        await RunOnVMContextAsync(() => dropped = _copyCard is { IsCopyReview: true } shown && ReferenceEquals(shown.CopyRun, copy)
                                                      ? shown.DroppedKeys() : null);
        CopyReview review;
        try { review = await copy.ReviewAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("Copy.Review", ex);
            review = new CopyReview(0, copy.Copy.Branch, [], "the copy's changes could not be read: " + Diagnostics.RootMessage(ex));
        }
        return ChatMessageItem.CopyReviewMsg(copy, review, dropped,
            apply: kept => _ = ApplyCopyAsync(copy, review.Id, kept),
            discard: () => _ = DiscardCopyAsync(copy));
    }

    private async Task ShowCopyReviewAsync(CopyRun copy, CancellationToken ct)
    {
        var card = await CopyReviewCardAsync(copy, ct);
        await RunOnVMContextAsync(() => ShowCopyCard(card!));
    }

    /// <summary>The review card's Apply: the kept changes written in the person's files, in a run of their own
    /// (<c>/undo-run</c> gives them back). Refused, the copy is read again: the review shown may be old.</summary>
    private Task ApplyCopyAsync(CopyRun copy, int reviewId, IReadOnlyDictionary<string, IReadOnlyCollection<int>> kept) =>
        CopyGestureAsync(async () =>
        {
            if (copy.Discarded)
            {
                await RunOnVMContextAsync(() => { RemoveCopyCard(); InsertThemed(ChatMessageItem.NoticeMsg(Strings.CopyNoneOpen)); });
                return;
            }
            // The editor's unsaved buffers, read now: a file the person is editing is left and named, never written under it.
            if (_tools.Overlay is { } overlay) await overlay.RefreshAsync(CancellationToken.None);
            var outcome = await copy.ApplyAsync(reviewId, kept, _tools.History, CancellationToken.None,
                                                path => _tools.Overlay?.TryGetUnsaved(path, out _) == true);
            if (outcome.Applied is not { } applied)
            {
                var again = await CopyReviewCardAsync(copy, CancellationToken.None);
                await RunOnVMContextAsync(() =>
                {
                    InsertThemed(ChatMessageItem.NoticeMsg(outcome.Refusal ?? Strings.CopyNoneOpen));
                    ShowCopyCard(again!);
                });
                return;
            }
            var card = CopyCards.Applied(applied, kept, copy.LastReview!, copy.Copy, copy.Inside, copy.Workspace);
            await RunOnVMContextAsync(() => ShowCopyCard(ChatMessageItem.CopyAppliedMsg(card,
                compare: file => _ = CompareWithCopyAsync(file),
                merge: file => Post(() => Prompt = file.MergePrompt),
                discard: () => _ = DiscardCopyAsync(copy),
                keep: () => Post(() =>
                {
                    RemoveCopyCard();
                    InsertThemed(ChatMessageItem.NoticeMsg(card.KeptSaid));
                }))));
        });

    /// <summary>
    /// The notice of a start's gestures on the copy it named: exactly that copy taken up, then its review or its discard as
    /// the card does. Taken up, it is held: left again later, a start says it again. ⚠ Refused (another window took it up,
    /// this window holds another copy of the repository), said — never silent.
    /// </summary>
    private async Task ActOnLeftCopyAsync(string folder, bool discard)
    {
        try
        {
            PinWorkspaceRoot();
            CopyRun? copy;
            if (HeldCopy is { } held)
            {
                if (!PathComparer.SameDirectory(held.Copy.Folder, folder))
                {
                    await ShowInfoAsync(Strings.CopyAlreadyHeld(held.Copy.Branch));
                    return;
                }
                copy = held;
            }
            else if ((copy = await CopyRun.TakeUpAsync(folder, _indexService.RootDir, ConversationStore.VisualStudioSlot,
                                                       CancellationToken.None)) is null)
            {
                await ShowInfoAsync(Strings.CopyNoneOpen);
                return;
            }
            Hold(copy);
            if (discard) await DiscardCopyAsync(copy);
            else await ShowCopyReviewAsync(copy, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("Copy.LeftNotice", ex);
            await ShowInfoAsync(Strings.MsgError(Diagnostics.RootMessage(ex)));
        }
    }

    /// <summary>"Discard the copy": the copy and its branch removed — refused while a question is answered in it.</summary>
    private Task DiscardCopyAsync(CopyRun copy) => CopyGestureAsync(async () =>
    {
        var (done, problem) = await copy.DiscardAsync(CancellationToken.None);
        await RunOnVMContextAsync(() =>
        {
            if (!done)
            {
                InsertThemed(ChatMessageItem.NoticeMsg(problem ?? Strings.CopyInUse));
                CopyCardBack();
                return;
            }
            if (_copies.TryGetValue(CopyKey(copy.Workspace), out var held) && ReferenceEquals(held, copy)) _copies.Remove(CopyKey(copy.Workspace));
            RemoveCopyCard();
            InsertThemed(ChatMessageItem.NoticeMsg(problem is null ? Strings.CopyDiscarded(copy.Copy.Branch) : Strings.CopyDiscardedBut(problem)));
        });
    });

    /// <summary>
    /// "Compare with my version": the copy's file beside the person's in Visual Studio's comparison tab, by the tool git
    /// uses for it (<c>vsDiffMerge.exe /t</c>, a tab in the open Visual Studio).
    /// </summary>
    /// <remarks>shortcut: with several Visual Studio windows open, the tab may open in another one — the tool picks the
    /// instance; a request through the in-process package would name it.</remarks>
    private async Task CompareWithCopyAsync(CopyNotAppliedFile file)
    {
        try
        {
            var (pid, _) = ParentProcess.FindOwningDevenv();
            using var devenv = pid is int id ? Process.GetProcessById(id) : null;
            var ide = devenv?.MainModule?.FileName is { } exe ? Path.GetDirectoryName(exe) : null;
            var tool = ide is null ? null
                : Path.Combine(ide, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", "vsDiffMerge.exe");
            if (tool is not null && File.Exists(tool))
            {
                var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { file.CopyPath, file.MinePath, "/t" })
                    start.ArgumentList.Add(argument);
                using var _ = Process.Start(start);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("Copy.Compare", ex); }
        await ShowInfoAsync(Strings.CopyCompareUnavailable(file.CopyPath, file.MinePath));
    }
}
