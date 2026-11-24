using System.IO;
using System.Text;

namespace Inferpal.Services.Execution;

/// <summary>What the start card of a run on a copy says, before anything is made.</summary>
/// <param name="Refusal">Why no copy can be made of this folder (no git repository, no commit, git did not answer), in English
/// — the card frames it; <c>null</c> when one can.</param>
/// <param name="Commit">The commit the copy starts from, short.</param>
/// <param name="Branch">The person's branch at that commit; <c>null</c> on a detached HEAD.</param>
/// <param name="Uncommitted">The changes the person has not committed: they do not go into the copy; <c>null</c> when git
/// could not list them (said, never read as none).</param>
internal sealed record CopyStart(string? Refusal, string? Commit, string? Branch, IReadOnlyList<string>? Uncommitted);

/// <summary>One file the run changed in the copy, as the review lists it: its path in the repository, whether the run created
/// or deleted it, and its hunks — each kept or dropped on its own.</summary>
internal sealed record CopyReviewFile(string Path, bool Created, bool Deleted, IReadOnlyList<CodeActions.DiffHunk> Hunks);

/// <summary>What the run left in its copy, for the review.</summary>
/// <param name="Id">This review's number: an apply names the review it keeps hunks of, and is refused once the copy was read
/// again since — what the person kept is what they saw.</param>
/// <param name="Branch">The copy's branch, where what is not applied stays until the copy is discarded.</param>
/// <param name="Files">The changed files of the folder the person opened, in the order git lists them.</param>
/// <param name="Problem">What the review leaves out and why (unread, binary, outside the folder), in English; <c>null</c> when
/// nothing.</param>
internal sealed record CopyReview(int Id, string Branch, IReadOnlyList<CopyReviewFile> Files, string? Problem);

/// <summary>What an apply did, or why it did nothing (<see cref="Refusal"/>, translated).</summary>
internal sealed record CopyApplyOutcome(CopyApplied? Applied, string? RunId, string? Refusal);

/// <summary>
/// A run on a copy of the repository (pari 106), from its start card to the person's choice: made at the last commit, the
/// run entered there (<see cref="RunRoot"/>), its changes reviewed hunk by hunk, the hunks kept applied to the person's files
/// in a run of their history (<c>/undo-run</c> gives them back), the copy discarded or kept for now.
/// </summary>
/// <remarks>⚠ One per session at a time, held by it: every review, apply or discard reaches that copy only, never one another
/// session holds. ⚠ A copy a question is being answered in is never discarded (<see cref="Use"/>).</remarks>
internal sealed class CopyRun
{
    private IReadOnlyList<CopyChange>? _changes;
    private int _reviewId;
    private int _inUse;

    /// <summary>What every run in this copy files its agreements under: this copy's alone, the same for a follow-up.</summary>
    private readonly string _scope;

    /// <summary>The folder the person opened, as a path inside the repository (<c>""</c> at its top), with a trailing '/'.</summary>
    private readonly string _inside;

    private CopyRun(WorkCopy copy, string workspace, bool leftBefore)
    {
        Copy = copy;
        Workspace = workspace;
        LeftBefore = leftBefore;
        _scope = $"{Path.GetFullPath(copy.Folder)}#{Guid.NewGuid():N}";
        // On the paths links lead to, as RunRoot judges it: git gives the repository resolved, the editor the folder as opened.
        var inside = Path.GetRelativePath(Tools.PathSanitizer.Canonical(Path.GetFullPath(copy.Repository)),
                                          Tools.PathSanitizer.Canonical(Path.GetFullPath(workspace))).Replace('\\', '/');
        _inside = inside == "." ? "" : inside.TrimEnd('/') + "/";
        // Built from its own files from the first question: an index of an earlier copy in the same folder is old code.
        Lsp.CSharpSemanticIndex.Forget(Path.GetFullPath(copy.Folder));
    }

    /// <summary>Whether an earlier session left this copy (the editor closed, the host stopped): said, never removed silently.</summary>
    internal bool LeftBefore { get; }

    /// <summary>The copy the run works in.</summary>
    internal WorkCopy Copy { get; }

    /// <summary>The folder the person opened, which the run's root copies.</summary>
    internal string Workspace { get; }

    /// <summary>Whether the copy has been discarded: nothing more reaches it.</summary>
    internal bool Discarded { get; private set; }

    /// <summary>The review read last: what an apply keeps hunks of, and where the card after it places them.</summary>
    internal CopyReview? LastReview { get; private set; }

    /// <summary>The commit the copy started from, short: what a card names.</summary>
    internal string StartCommit => Copy.BaseCommit[..Math.Min(7, Copy.BaseCommit.Length)];

    /// <summary>The folder the person opened, as a path in the repository: <c>""</c> at its top, else ending in '/'.</summary>
    internal string Inside => _inside;

    /// <summary>What the start card says for <paramref name="workspace"/>; nothing is made.</summary>
    internal static async Task<CopyStart> PrepareAsync(string workspace, CancellationToken ct)
    {
        // ⚠ No folder open: git would run where the process happens to be, and copy THAT repository.
        if (string.IsNullOrWhiteSpace(workspace))
            return new CopyStart(NoFolder, null, null, null);
        var head = await GitProcess.CaptureBytesAsync(["rev-parse", "--verify", "--short", "HEAD"], workspace, ct, WorkCopy.MaxFileBytes);
        if (head.Output is null)
        {
            return new CopyStart(WorkCopy.NoHead(head.Failure), null, null, null);
        }
        var branch = await WorkCopy.GitText(["symbolic-ref", "--short", "-q", "HEAD"], workspace, ct);
        var uncommitted = await WorkCopy.UncommittedAsync(workspace, ct);
        return new CopyStart(null, Encoding.UTF8.GetString(head.Output).Trim(), string.IsNullOrEmpty(branch) ? null : branch, uncommitted);
    }

    /// <summary>Makes the copy of <paramref name="workspace"/>'s repository, held for <paramref name="editor"/>; the refusal says
    /// why when it cannot.</summary>
    internal static async Task<(CopyRun? Run, string? Refusal)> StartAsync(string workspace, CancellationToken ct, string editor = "")
    {
        if (string.IsNullOrWhiteSpace(workspace)) return (null, NoFolder);
        var (copy, refusal) = await WorkCopy.CreateAsync(workspace, ct, editor);
        return copy is null ? (null, refusal) : (new CopyRun(copy, workspace, leftBefore: false), null);
    }

    /// <summary>
    /// The copy of <paramref name="workspace"/>'s repository an earlier session left (its holder gone), taken up by this
    /// process, oldest first; <c>null</c> when none.
    /// </summary>
    internal static async Task<CopyRun?> FindLeftAsync(string workspace, CancellationToken ct, string editor = "")
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        if ((await WorkCopy.FindAsync(workspace, ct)).FirstOrDefault() is not { } copy) return null;
        await copy.TakeOverAsync(ct, editor);
        return new CopyRun(copy, workspace, leftBefore: true);
    }

    /// <summary>
    /// The copy left behind in <paramref name="folder"/> — exactly the one a notice named, never another — taken up by this
    /// process for <paramref name="editor"/>; <c>null</c> when it is not left there any more (taken up by another window,
    /// discarded).
    /// </summary>
    internal static async Task<CopyRun?> TakeUpAsync(string folder, string workspace, string editor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || await WorkCopy.LeftAtAsync(workspace, folder, ct) is not { } copy) return null;
        await copy.TakeOverAsync(ct, editor);
        return new CopyRun(copy, workspace, leftBefore: true);
    }

    /// <summary>Enters the copy for the current flow (<see cref="RunRoot.Enter(WorkCopy, string, string)"/>): the run's tools,
    /// approvals and system prompt work there until the scope is disposed; a follow-up keeps the agreements and the index.</summary>
    internal IDisposable Enter() => RunRoot.Enter(Copy, Workspace, _scope);

    /// <summary>Marks a question being answered in the copy, from before its turn until its review is read: no discard meanwhile.</summary>
    internal IDisposable Use()
    {
        Interlocked.Increment(ref _inUse);
        return new Released(this);
    }

    private sealed class Released(CopyRun owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._inUse);
        }
    }

    /// <summary>
    /// What the run changed in the folder the person opened, for the review; read again at each call (a follow-up may have
    /// changed it), each read numbered. A file changed outside that folder is said, never offered: applied, it would write
    /// where the person did not open.
    /// </summary>
    internal async Task<CopyReview> ReviewAsync(CancellationToken ct)
    {
        var (changes, problem) = await Copy.ChangesAsync(ct);
        // A change with no line to keep or drop (an empty file created or deleted, a mode changed) is said, never offered:
        // shown with nothing to tick, it could never be applied.
        var lineless = changes.Where(c => c.Plan.Hunks.Count == 0).Select(c => c.Path).ToList();
        if (lineless.Count > 0)
        {
            var note = $"{lineless.Count} {(lineless.Count == 1 ? "file" : "files")} changed without a line to keep or drop (an "
                     + $"empty file, or only its mode), not offered: {string.Join(", ", lineless)} (on the branch {Copy.Branch})";
            problem = problem is null ? note : problem + "; " + note;
            changes = [.. changes.Where(c => c.Plan.Hunks.Count > 0)];
        }
        var inside = changes.Where(c => c.Path.StartsWith(_inside, PathComparer.Comparison)).ToList();
        var outside = changes.Count - inside.Count;
        if (outside > 0)
        {
            var note = $"{outside} {(outside == 1 ? "file" : "files")} changed outside the folder you opened, not offered: "
                     + string.Join(", ", changes.Where(c => !c.Path.StartsWith(_inside, PathComparer.Comparison)).Select(c => c.Path))
                     + $" (on the branch {Copy.Branch})";
            problem = problem is null ? note : problem + "; " + note;
        }
        _changes = inside;
        var id = Interlocked.Increment(ref _reviewId);
        var files = inside.Select(c => new CopyReviewFile(c.Path, c.Before is null, c.After is null, c.Plan.Hunks)).ToList();
        return LastReview = new CopyReview(id, Copy.Branch, files, problem);
    }

    /// <summary>
    /// Writes the kept hunks of review <paramref name="reviewId"/> (<paramref name="kept"/>: by path, the hunk indexes) into
    /// the person's files, in a run of <paramref name="history"/> of its own: each file backed up first, so <c>/undo-run</c>
    /// gives them back. A file the person changed since the copy was made, or holds unsaved in the editor
    /// (<paramref name="unsaved"/>), is left as it is and named (<see cref="CopyLeftFile"/>).
    /// </summary>
    /// <remarks>⚠ Refused once the copy was read again since that review: what the person kept is what they saw.</remarks>
    internal async Task<CopyApplyOutcome> ApplyAsync(int reviewId, IReadOnlyDictionary<string, IReadOnlyCollection<int>> kept,
                                                     FileHistoryService history, CancellationToken ct, Func<string, bool>? unsaved = null)
    {
        if (Discarded) throw new InvalidOperationException("the copy was discarded");
        if (_changes is null || reviewId != Volatile.Read(ref _reviewId))
            return new CopyApplyOutcome(null, null, Localization.Strings.CopyReviewChanged);
        var runId = history.BeginRun();
        try { return new CopyApplyOutcome(await WorkCopy.ApplyAsync(Copy.Repository, _changes, kept, history, ct, unsaved), runId, null); }
        finally { history.EndRun(); }
    }

    /// <summary>Removes the copy and its branch; what could not be removed is said, <c>null</c> when all went. Refused (the
    /// copy kept) while a question is being answered in it.</summary>
    internal async Task<(bool Done, string? Problem)> DiscardAsync(CancellationToken ct)
    {
        if (Discarded) return (true, null);
        if (Volatile.Read(ref _inUse) > 0) return (false, Localization.Strings.CopyInUse);
        var problem = await Copy.DiscardAsync(ct);
        Discarded = true;
        Lsp.CSharpSemanticIndex.Forget(Path.GetFullPath(Copy.Folder));
        return (true, problem);
    }

    /// <summary>Why no copy is made with no folder open.</summary>
    private const string NoFolder = "no folder is open: a copy is made of the folder open in the editor";
}
