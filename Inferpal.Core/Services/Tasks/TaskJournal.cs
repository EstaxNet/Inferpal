using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Tasks;

/// <summary>
/// The background tasks of one workspace in one editor, kept on disk: a task that finished — its report, its proposals —
/// outlives the process that ran it, and a number given once is never given again.
/// </summary>
/// <remarks>
/// <para>⚠ The file is SHARED: every window of one editor, and every Agent Client Protocol thread, writes it. Every change
/// is a read, a change and a write under one lock file, and touches only the entries it names (<see cref="Update"/>):
/// written from a copy read earlier, a window erases the tasks the other added since, and two of them give the same
/// number.</para>
/// <para>⚠ An unfinished entry belongs to the queue that wrote it while that queue lives (<see cref="Owner"/>): another
/// window's running task is neither shown here nor called stopped. Its owner gone, it is a task the closing stopped.</para>
/// </remarks>
internal sealed class TaskJournal(string path)
{
    /// <summary>Where the journal lives.</summary>
    public string Path { get; } = path;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The queue that writes an entry: its process, when that process started, and the queue itself.</summary>
    internal sealed record Owner(
        [property: JsonPropertyName("pid")]   int Pid,
        [property: JsonPropertyName("since")] long Since,
        [property: JsonPropertyName("queue")] string Queue);

    /// <summary>One task as the journal keeps it.</summary>
    /// <param name="Read">Its report was opened (<c>/task &lt;id&gt;</c>).</param>
    /// <param name="Updated">When the entry was last written: for a task the closing stopped, the last moment known of it.</param>
    /// <param name="Said">The notice of a start said it, and the person dismissed it: it is not said again.</param>
    internal sealed record Entry(
        [property: JsonPropertyName("id")]          string Id,
        [property: JsonPropertyName("objective")]   string Objective,
        [property: JsonPropertyName("state")]       BackgroundTaskState State,
        [property: JsonPropertyName("created")]     DateTimeOffset CreatedAt,
        [property: JsonPropertyName("owner")]       Owner? By = null,
        [property: JsonPropertyName("started")]     DateTimeOffset? StartedAt = null,
        [property: JsonPropertyName("finished")]    DateTimeOffset? FinishedAt = null,
        [property: JsonPropertyName("result")]      string? Result = null,
        [property: JsonPropertyName("error")]       string? Error = null,
        [property: JsonPropertyName("cause")]       TaskEndCause Cause = TaskEndCause.None,
        [property: JsonPropertyName("steps")]       IReadOnlyList<string>? Steps = null,
        [property: JsonPropertyName("stepsTaken")]  int StepsTaken = 0,
        [property: JsonPropertyName("propose")]     bool ProposeWrites = false,
        [property: JsonPropertyName("proposals")]   IReadOnlyList<SavedProposal>? Proposals = null,
        [property: JsonPropertyName("context")]     string? Context = null,
        [property: JsonPropertyName("workspace")]   string? WorkspaceRoot = null,
        [property: JsonPropertyName("read")]        bool Read = false,
        [property: JsonPropertyName("updated")]     DateTimeOffset? Updated = null,
        [property: JsonPropertyName("said")]        bool Said = false)
    {
        /// <summary>Whether the task reached a state nothing changes any more.</summary>
        [JsonIgnore]
        public bool IsFinished => State is BackgroundTaskState.Succeeded or BackgroundTaskState.Failed
                                        or BackgroundTaskState.Cancelled;
    }

    /// <summary>A proposal as the journal keeps it.</summary>
    internal sealed record SavedProposal(
        [property: JsonPropertyName("tool")]    string Tool,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("details")] string Details,
        [property: JsonPropertyName("old")]     string? OldText = null,
        [property: JsonPropertyName("new")]     string? NewText = null,
        [property: JsonPropertyName("file")]    string? FilePath = null)
    {
        public static SavedProposal Of(TaskProposal p) =>
            new(p.Tool, p.Subject, p.Details, p.Diff?.OldText, p.Diff?.NewText, p.Diff?.FilePath);

        public TaskProposal ToProposal() =>
            new(Tool, Subject, Details, OldText is null && NewText is null ? null : new DiffInfo(OldText ?? "", NewText ?? "", FilePath ?? ""));
    }

    /// <param name="Last">The highest number ever given: <c>/task clear</c> forgets tasks, never their numbers.</param>
    /// <param name="SaidRuns">The runs the closing cut that the notice of a start said and the person dismissed.</param>
    private sealed record Document(
        [property: JsonPropertyName("tasks")]    List<Entry>? Tasks,
        [property: JsonPropertyName("last")]     int Last = 0,
        [property: JsonPropertyName("saidRuns")] List<string>? SaidRuns = null);

    /// <summary>The runs a dismissed notice already said, so a start never says them again.</summary>
    public IReadOnlySet<string> SaidRuns()
    {
        try
        {
            using var held = Lock();
            return File.Exists(Path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), Options)?.SaidRuns is { } said
                ? said.ToHashSet(StringComparer.Ordinal) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // ⚠ Unreadable is taken as "nothing said" on purpose: a dismissed notice may come back once, where "unknown"
            // would have to withhold the notice of a run the closing really cut. Saying twice is the lesser harm.
            Diagnostics.Swallow("TaskJournal.SaidRuns", ex);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Records runs and tasks a dismissed notice said; the newest hundred runs are kept.</summary>
    public void MarkSaid(IEnumerable<string> runIds, IEnumerable<string> taskIds)
    {
        var tasks = taskIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Change((entries, _) =>
        {
            for (var i = 0; i < entries.Count; i++)
                if (tasks.Contains(entries[i].Id)) entries[i] = entries[i] with { Said = true };
            return true;
        }, [.. runIds]);
    }

    /// <summary>The number of a task id (<c>t12</c> → 12); 0 for anything else.</summary>
    internal static int Number(string? id) =>
        id is { Length: > 1 } && (id[0] is 't' or 'T') && int.TryParse(id.AsSpan(1), System.Globalization.NumberStyles.None,
                                                                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>
    /// Gives the next number — above every number this journal ever gave and above <paramref name="known"/> — and writes
    /// the entry <paramref name="entryFor"/> makes for it, in one step under the lock. <c>null</c> when the journal could
    /// not be written.
    /// </summary>
    public string? Reserve(int known, Func<string, Entry> entryFor) =>
        Change((entries, last) =>
        {
            var id = "t" + (Math.Max(known, Math.Max(last, entries.Select(e => Number(e.Id)).DefaultIfEmpty(0).Max())) + 1);
            entries.Add(entryFor(id));
            return id;
        });

    /// <summary>
    /// The entries, and the path of the copy kept of a journal that could not be read (<c>null</c> when it was read, or
    /// absent). A journal that cannot be read is set aside once, never written over: the reports it holds may be the only
    /// copy.
    /// </summary>
    /// <returns><c>Entries</c> is <c>null</c> when the journal could not be read at all (its lock held too long, a disk
    /// error): never an empty journal.</returns>
    public (List<Entry>? Entries, string? SetAside) Read()
    {
        try
        {
            using var held = Lock();
            var (entries, _, _, aside) = ReadLocked();
            return (entries, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("TaskJournal.Read", ex);
            return (null, null);
        }
    }

    private string? _setAside;

    /// <summary>The copy a change of this journal had to set aside (a journal that could not be read), once: the caller
    /// says it.</summary>
    public string? TakeSetAside() => Interlocked.Exchange(ref _setAside, null);

    /// <summary>
    /// Reads the journal, lets <paramref name="change"/> change its entries, and writes them — under the lock, so nothing
    /// written by another window in between is lost. Best effort: a failure is traced, the caller keeps its tasks in
    /// memory.
    /// </summary>
    /// <returns>What <paramref name="change"/> returned, or <c>default</c> when the journal could not be written.</returns>
    public T? Update<T>(Func<List<Entry>, T> change) => Change((entries, _) => change(entries));

    private T? Change<T>(Func<List<Entry>, int, T> change, IReadOnlyCollection<string>? saidRuns = null)
    {
        try
        {
            using var held = Lock();
            var (entries, last, said, aside) = ReadLocked();
            if (aside is not null) _setAside = aside;
            var result = change(entries, last);
            last = Math.Max(last, entries.Select(e => Number(e.Id)).DefaultIfEmpty(0).Max());
            if (saidRuns is { Count: > 0 })
                said = [.. said.Concat(saidRuns).Distinct(StringComparer.Ordinal).TakeLast(100)];
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            Persistence.AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(
                new Document(entries, last, said.Count > 0 ? said : null), Options));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Swallow("TaskJournal.Update", ex);
            return default;
        }
    }

    private (List<Entry> Entries, int Last, List<string> SaidRuns, string? SetAside) ReadLocked()
    {
        if (!File.Exists(Path)) return ([], 0, [], null);
        var text = File.ReadAllText(Path);
        try
        {
            // An entry without an id or an objective (edited by hand) is no task: left out, never a reason to fail.
            if (JsonSerializer.Deserialize<Document>(text, Options) is { Tasks: { } tasks } document)
                return ([.. tasks.Where(e => e is { Id.Length: > 0, Objective: not null })], document.Last,
                        document.SaidRuns ?? [], null);
        }
        catch (JsonException) { }
        // Kept as it was before anything is written over it — and the numbers it gave stay given.
        var aside = Path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Copy(Path, aside, overwrite: true);
        File.Delete(Path);
        Diagnostics.Record("TaskJournal", "The background tasks journal could not be read; a copy is kept at " + aside);
        var given = System.Text.RegularExpressions.Regex.Matches(text, "\"(?:id\"\\s*:\\s*\"t|last\"\\s*:\\s*)(\\d{1,9})")
                          .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                          .DefaultIfEmpty(0).Max();
        Persistence.AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(new Document([], given), Options));
        return ([], given, [], aside);
    }

    /// <summary>The lock every reader and writer of this journal takes: one at a time, across processes.</summary>
    /// <remarks>⚠ The lock file stays: deleted on close, a process that opened it just before could lock the deleted file
    /// while another created and locked a new one — two writers at once outside Windows, where the share mode is an
    /// advisory lock on one file.</remarks>
    private IDisposable Lock()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            // A lock file still being released (or scanned) can refuse access for a moment on Windows: waited for too.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100) { Thread.Sleep(20); }
        }
    }
}

/// <summary>Why a task stopped short, when it was not its own doing nor the person's.</summary>
internal enum TaskEndCause
{
    /// <summary>It ended on its own, or the person stopped it.</summary>
    None,
    /// <summary>It was queued or running when the editor closed.</summary>
    EditorClosed,
    /// <summary>The model server stopped answering during its run.</summary>
    ServerStopped,
}
