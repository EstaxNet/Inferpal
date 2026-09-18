using System.IO;
using System.Text;

namespace Inferpal.Services.Tools;

/// <summary>
/// The one way the editing tools write a text file: re-encoded exactly as it was found.
/// </summary>
/// <remarks>
/// <c>File.WriteAllTextAsync(path, content)</c> always emits UTF-8 without BOM — so every
/// one-line <c>apply_diff</c> stripped the BOM Visual Studio puts on .cs/.resx files (whole-file
/// churn in git, "file completely changed" hooks) and silently transcoded UTF-16 files
///. This helper detects the existing
/// file's BOM before writing and re-writes with the same encoding; a <em>new</em> file gets
/// UTF-8 without BOM, the modern default. Detection is BOM-based only: a BOM-less file is
/// treated as UTF-8, exactly what the read path (<c>File.ReadAllTextAsync</c>) already assumes,
/// so this can never regress a file the tools could read correctly in the first place.
/// </remarks>
internal static class SafeFileWriter
{
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The encoding an existing file's BOM declares — UTF-8 without BOM when it has none.</summary>
    internal static Encoding DetectEncoding(string path)
    {
        Span<byte> bom = stackalloc byte[4];
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = fs.Read(bom);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Swallow($"SafeFileWriter.DetectEncoding({path})", ex);
            return Utf8NoBom;   // unreadable now → the write itself will surface the real error
        }

        // Order matters: UTF-32 LE starts with the UTF-16 LE BOM.
        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00) return Encoding.UTF32;
        if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;             // UTF-8 with BOM
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;                             // UTF-16 LE
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;                    // UTF-16 BE
        return Utf8NoBom;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, keeping the encoding (and BOM)
    /// the existing file carries; a new file is written UTF-8 without BOM.
    /// </summary>
    /// <remarks>⚠ Cancellation is honoured BEFORE the write, never during it:
    /// <c>File.WriteAllTextAsync</c> truncates the file first, so a Stop landing mid-write left a
    /// truncated file behind an approved change.</remarks>
    internal static Task WritePreservingAsync(string path, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var encoding = File.Exists(path) ? DetectEncoding(path) : Utf8NoBom;
        return File.WriteAllTextAsync(path, content, encoding, CancellationToken.None);
    }

    /// <summary>Outcome of a multi-file write: which one failed, why, and what could not be put back.</summary>
    /// <param name="Stuck">
    /// Files written then impossible to restore — the state no rollback can repair, which is why it
    /// is named rather than counted.
    /// </param>
    internal readonly record struct MultiWriteResult(
        string? FailedPath, string? Error, IReadOnlyList<string> Stuck)
    {
        public bool Ok => FailedPath is null;
    }

    /// <summary>
    /// Writes every file, or none: the first failure puts back each file already written.
    /// </summary>
    /// <remarks>
    /// ⚠ The funnel for every tool that writes <b>several</b> files, because the alternative is what
    /// <c>rename_symbol</c> did — collect the error, keep going, and report <i>"Applied with 1
    /// error(s)"</i> over a workspace where the symbol is renamed in eight files out of nine. A
    /// partially applied rename is the one refactor whose half state never compiles, and
    /// <c>apply_edits</c> three files away already had the answer: its comment carries the reason,
    /// <i>"the description promises the model 'if ANY edit cannot be applied, NO file is changed'"</i>.
    /// ⚠ Deliberately not cancellable: the caller has already prompted and backed up, and a Stop
    /// landing between two writes is exactly the half-applied state this exists to prevent.
    /// </remarks>
    internal static async Task<MultiWriteResult> WriteAllOrRollBackAsync(
        IReadOnlyList<(string Path, string Content, string Original)> files)
    {
        var written = new List<(string Path, string Original)>();
        foreach (var (path, content, original) in files)
        {
            try
            {
                await WritePreservingAsync(path, content, CancellationToken.None).ConfigureAwait(false);
                written.Add((path, original));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var stuck = new List<string>();
                foreach (var (done, original0) in written)
                {
                    try { await WritePreservingAsync(done, original0, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                    {
                        Diagnostics.Swallow("SafeFileWriter.RollBack", rollback);
                        stuck.Add(done);
                    }
                }
                return new MultiWriteResult(path, ex.Message, stuck);
            }
        }
        return new MultiWriteResult(null, null, []);
    }
}
