namespace Inferpal.Services.Governance;

/// <summary>
/// The workspace's ignore files declared for what Visual Studio has open, in a process where no chat window declares
/// them: the completion sidecar, and the completion key's command before the chat was ever opened.
/// </summary>
/// <remarks>The root is the open solution's extent, or the folder Visual Studio has open, as the chat window decides it.
/// Declared when what is open changes, not at each request: the extent reads the solution and the repository, and a
/// declaration reads the ignore files again.</remarks>
internal sealed class OpenWorkspaceExclusions(Func<string?> files)
{
    private string? _declaredFor;

    /// <summary>Declares the ignore files of what is open now, when it changed since the last call.</summary>
    public void Follow()
    {
        var open = ActiveSolutionSignal.TryReadSolutionPath() ?? ActiveSolutionSignal.TryReadOpenFolder();
        if (string.Equals(open, _declaredFor, StringComparison.Ordinal)) return;
        _declaredFor = open;
        if (SolutionExtent.ActiveRoot() is { } root) ContentExclusions.Declare(root, files);
    }
}
