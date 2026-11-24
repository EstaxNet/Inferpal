namespace Inferpal.Services.Governance;

/// <summary>Why a file of the repository was not read.</summary>
internal enum RepoFileProblem
{
    /// <summary>Read.</summary>
    None,

    /// <summary>A link that leaves the repository: a cloned link to <c>~/.ssh</c> would send its keys to the model.</summary>
    LeavesTheRepository,

    /// <summary>Past the size cap: the detail says how large.</summary>
    TooLarge,

    /// <summary>The file could not be read: the detail says why.</summary>
    Unreadable,
}

/// <summary>
/// A file a cloned repository controls, read into the prompt (a mode, a rule, a check, a prompt file): never through a link
/// that leaves the repository, never past a size cap — one reader for every such file.
/// </summary>
internal static class RepoFile
{
    /// <summary>The text of <paramref name="path"/>, or why it was not read.</summary>
    /// <param name="confineTo">The repository; <c>null</c>: a file of the person's own, not confined.</param>
    internal static (string? Text, RepoFileProblem Problem, string? Detail) Read(string path, string? confineTo, long maxBytes)
    {
        try
        {
            if (confineTo is not null)
            {
                try { Tools.PathSanitizer.AssertUnderRoot(path, confineTo); }
                catch (ArgumentException) { return (null, RepoFileProblem.LeavesTheRepository, null); }
            }
            var size = new FileInfo(path).Length;
            if (size > maxBytes) return (null, RepoFileProblem.TooLarge, $"{size} bytes, over {maxBytes}");
            return (Tools.TextFileEncoding.ReadText(path), RepoFileProblem.None, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, RepoFileProblem.Unreadable, ex.Message);
        }
    }
}
