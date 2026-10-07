using System.Collections.Generic;

namespace Inferpal.GhostText;

/// <summary>Whether a pause in typing asks the model for a ghost-text suggestion.</summary>
/// <remarks>
/// ⚠ The completion list owns the caret while it is open — Tab commits it —, and that is the only conflict: the
/// broker answers it (<paramref name="completionActive"/> of <see cref="Asks"/>). The characters that OPEN the list
/// (<c>.</c>, <c>(</c>, a space…) are no reason to stay silent: they come right before the positions a suggestion is
/// worth most at — after <c>return </c>, after <c>= </c>, after <c>user.</c>, at the start of an indented line — and
/// VS Code, which never filtered them, suggests there. Only without the broker (an optional import) can the list be
/// open without our knowing; those characters are then the only sign of it left.
/// </remarks>
internal static class GhostTextTrigger
{
    private static readonly HashSet<char> ListOpeners = ['.', '(', '[', '<', '"', '\'', ',', ' '];

    /// <param name="previous">The character before the caret; <c>null</c> at the start of the document.</param>
    /// <param name="brokerKnown">Whether the editor's completion broker was imported.</param>
    /// <param name="completionActive">Whether its completion list is open in this view.</param>
    internal static bool Asks(char? previous, bool brokerKnown, bool completionActive)
    {
        if (completionActive) return false;
        if (brokerKnown) return true;
        return previous is not { } c || !ListOpeners.Contains(c);
    }
}
