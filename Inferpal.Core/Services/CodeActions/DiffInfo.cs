namespace Inferpal.Services.CodeActions;

/// <summary>A change to approve: the text before and after, or a binary file's (<see cref="Binary"/>).</summary>
internal record DiffInfo(string OldText, string NewText, string FilePath = "")
{
    /// <summary>A binary file's change — a capture saved as PNG: said by its kind, size and picture, never as lines;
    /// <c>null</c> for a text change. Its texts are empty: there is no text to diff.</summary>
    public Presentation.RunFileBinary? Binary { get; init; }

    /// <summary>For a binary change: the file does not exist yet.</summary>
    public bool Creates { get; init; }
}
