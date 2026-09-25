using System.IO;
using System.Text;

namespace Inferpal.Services.Tools;

/// <summary>
/// The one way the editing tools write a text file: re-encoded exactly as it was found.
/// </summary>
/// <remarks>
/// <c>File.WriteAllTextAsync(path, content)</c> always emits UTF-8 without BOM — so every
/// one-line <c>apply_diff</c> stripped the BOM Visual Studio puts on .cs/.resx files (whole-file
/// churn in git, "file completely changed" hooks) and silently transcoded UTF-16 files. This
/// helper writes an existing file back in the encoding <see cref="TextFileEncoding"/> detects — the
/// one <see cref="TextFileEncoding.ReadTextAsync"/> read it in, legacy code pages included; a
/// <em>new</em> file gets UTF-8 without BOM, the modern default.
/// </remarks>
internal static class SafeFileWriter
{
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
        var encoding = File.Exists(path) ? TextFileEncoding.Detect(path) : TextFileEncoding.Utf8NoBom;
        // The net under the tools' own refusal: a legacy code page's fallback would write "?" or a look-alike instead.
        // An IOException, so a multi-file write puts the others back.
        if (TextFileEncoding.FirstUnrepresentable(encoding, content) is { } bad)
            throw new UnrepresentableTextException(FileTarget.CannotHold(path, encoding, bad.Character, bad.Line));
        return File.WriteAllTextAsync(path, content, encoding, CancellationToken.None);
    }

    /// <summary>A write refused because the file's encoding cannot hold one of the characters.</summary>
    internal sealed class UnrepresentableTextException(string message) : IOException(message);

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
