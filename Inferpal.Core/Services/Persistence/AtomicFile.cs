using System.IO;
using System.Text;

namespace Inferpal.Services.Persistence;

/// <summary>
/// Write-then-rename for the small JSON stores Inferpal keeps under <c>%AppData%</c>
/// (configuration, sessions, snippets, bench and arena results, MCP tokens).
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>File.WriteAllText</c> truncates the target before writing: a crash, a full disk or a
/// kill between the two leaves a half-written file — and for the configuration that means the
/// extension can no longer start. Staging into a sibling file and renaming makes the replacement
/// atomic on both NTFS and POSIX, so a reader only ever sees the old file or the new one.
/// </para>
/// <para>
/// <b>The staging name is unique per write, and that is not a detail.</b> A name derived only from
/// the target (<c>config.json.tmp</c>) turns every concurrent writer into a collision: the
/// configuration file is shared between Visual Studio and VS Code <i>by design</i>, and within one
/// process a settings save can land next to a <c>/model</c>. Two writers then either fight over the
/// same handle or rename a file the other already moved — an <see cref="IOException"/> escaping the
/// very helper written to keep writes safe. With a private staging file per write, the concurrent
/// case degrades to what it should be: last writer wins, both files whole.
/// </para>
/// <para>
/// <b>The rename itself still contends, and is retried.</b> Two replacements of the same
/// destination overlap in the Win32 rename, not just in the staging: the loser gets
/// <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> for a few milliseconds.
/// The rename stays atomic; it is only <i>attempted</i> more than once.
/// </para>
/// </remarks>
internal static class AtomicFile
{
    private static long _sequence;

    /// <summary>
    /// A staging path nobody else will pick: the process owns its id, and the counter separates
    /// its own overlapping writes — including two <c>async</c> ones resumed on the same thread,
    /// which a thread id would not have separated.
    /// </summary>
    private static string StagingPathFor(string path) =>
        $"{path}.{Environment.ProcessId}-{Interlocked.Increment(ref _sequence)}.tmp";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom   = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The encoding a rewrite uses: the <b>destination's own</b> byte-order mark when it already
    /// exists, UTF-8 with a mark for a new file — which is what every store this class was written
    /// for has always had.
    /// </summary>
    /// <remarks>
    /// ⚠ It used to be <c>Encoding.UTF8</c> unconditionally, which <b>emits</b> a mark, while the
    /// read side strips one: a rewrite therefore added three bytes at the head of any file that had
    /// none. Invisible for this class's own JSON stores — they are born here, so they all have the
    /// mark — but a plan is markdown a team commits, and <c>PlanDocument.WithStepDone</c> promises
    /// that "only the single checkbox character changes; every other byte of the file is preserved".
    /// Ticking a step on a hand-written plan showed up as a diff at the head of the file.
    /// </remarks>
    private static Encoding EncodingFor(string path)
    {
        try
        {
            if (!File.Exists(path)) return Utf8WithBom;
            // ⚠ NOT File.OpenRead: its FileShare.Read denies a rename, so peeking at the
            // destination made eight concurrent writers refuse each other — the very contention
            // this class exists to absorb, broken by the read added to serve it.
            // ConcurrentWriters_OfTheSameFile_NeitherThrowNorTear went red at once.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[3];
            return stream.Read(head) == 3 && head is [0xEF, 0xBB, 0xBF]
                ? Utf8WithBom
                : Utf8NoBom;
        }
        catch (Exception ex)
        {
            // An unreadable destination keeps the historical default rather than a guess: the
            // replacement below is what will fail, and it will say so.
            Diagnostics.Swallow("AtomicFile.EncodingFor", ex);
            return Utf8WithBom;
        }
    }

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="content"/>.</summary>
    /// <param name="preserveExistingMark">
    /// Keep the destination's own byte-order mark instead of always writing one. Off by default, and
    /// deliberately: reading the destination on <i>every</i> write widens the rename window enough
    /// for concurrent writers to start refusing each other, and the JSON stores this class was
    /// written for are all born here with a mark, so they have nothing to preserve. Only a file the
    /// <b>user</b> may have written needs it, and a single caller promises those bytes.
    /// </param>
    public static void WriteAllText(string path, string content, bool preserveExistingMark = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = StagingPathFor(path);
        try
        {
            File.WriteAllText(temp, content, preserveExistingMark ? EncodingFor(path) : Utf8WithBom);
            Replace(temp, path);
        }
        finally { Discard(temp); }
    }

    /// <inheritdoc cref="WriteAllText(string,string)"/>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default,
                                               bool preserveExistingMark = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = StagingPathFor(path);
        try
        {
            await File.WriteAllTextAsync(
                temp, content, preserveExistingMark ? EncodingFor(path) : Utf8WithBom, ct);
            await ReplaceAsync(temp, path, ct);
        }
        finally { Discard(temp); }
    }

    /// <inheritdoc cref="WriteAllText(string,string)"/>
    public static void WriteAllBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = StagingPathFor(path);
        try
        {
            File.WriteAllBytes(temp, content);
            Replace(temp, path);
        }
        finally { Discard(temp); }
    }

    /// <summary>How many times a contended rename is re-attempted before the caller hears about it.</summary>
    private const int RenameAttempts = 12;

    /// <summary>Backoff before attempt <paramref name="attempt"/>, capped so a save stays a save.</summary>
    private static int DelayMs(int attempt) => Math.Min(5 * attempt, 40);

    private static void Replace(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, path, overwrite: true); return; }
            catch (Exception ex) when (IsContention(ex) && attempt < RenameAttempts)
            {
                Thread.Sleep(DelayMs(attempt));
            }
        }
    }

    private static async Task ReplaceAsync(string temp, string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, path, overwrite: true); return; }
            catch (Exception ex) when (IsContention(ex) && attempt < RenameAttempts)
            {
                await Task.Delay(DelayMs(attempt), ct);
            }
        }
    }

    /// <summary>
    /// The two shapes a momentarily busy destination takes on Windows — and they are, honestly, the
    /// same two a permanently refused one takes: <c>ERROR_ACCESS_DENIED</c> does not say whether
    /// waiting would help. Hence the cap rather than a predicate: a genuine refusal costs a few
    /// hundred milliseconds before it surfaces unchanged. Only the errors waiting provably cannot
    /// fix are excluded, so a missing staging file or a vanished folder fails at once.
    /// </summary>
    private static bool IsContention(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && ex is not FileNotFoundException and not DirectoryNotFoundException);

    /// <summary>
    /// Removes a staging file that never made it to its destination. A unique name is never
    /// reclaimed by a later write, so a failed or cancelled save would otherwise leave debris under
    /// <c>%AppData%</c> for good. Pure cleanup: nothing to report, nothing to recover from.
    /// </summary>
    private static void Discard(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
    }

    /// <summary>
    /// Copies <paramref name="path"/> aside before it is overwritten, and returns the copy's path
    /// (<c>null</c> when there was nothing to copy or the copy failed). <b>The caller decides</b>
    /// that the file is unreadable — this helper only knows how to keep the bytes.
    /// </summary>
    /// <remarks>
    /// ⚠ An unreadable file is not merely <i>ignored</i>: the load returns an empty document or
    /// factory defaults, and the next save writes them over the original bytes. That is no longer a
    /// failed read, it is a loss — of a configuration (backends, per-role models, MCP servers,
    /// permission rules) or of a snippet library.
    ///
    /// Best-effort by construction: never prevent the save. Failing to archive is less serious than
    /// leaving the user unable to write.
    /// </remarks>
    internal static string? PreserveAside(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            if (File.Exists(aside)) return aside;   // already set aside within the same second
            File.Copy(path, aside);
            return aside;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("AtomicFile.PreserveAside", ex);
            return null;
        }
    }
}
