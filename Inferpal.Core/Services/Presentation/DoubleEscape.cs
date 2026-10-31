namespace Inferpal.Services.Presentation;

/// <summary>
/// Esc pressed twice in the composer stops a running turn, as the Stop button says ("Stop (Esc twice)"). One press alone
/// never stops: Esc also closes a popup or leaves a field, and a turn stopped by a stray key loses its work.
/// </summary>
/// <remarks>VS Code keeps the same window (<c>DOUBLE_ESCAPE_MS</c> in <c>vscode/src/webview/main.ts</c>), held to this one
/// by <c>DoubleEscapeTests</c>.</remarks>
internal sealed class DoubleEscape
{
    /// <summary>How close the second press must follow the first.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(800);

    private DateTime? _first;

    /// <summary>One press at <paramref name="now"/>: <c>true</c> when it is the second within <see cref="Window"/>, which
    /// stops; the first only arms.</summary>
    public bool Press(DateTime now)
    {
        if (_first is { } first && now - first >= TimeSpan.Zero && now - first <= Window)
        {
            _first = null;
            return true;
        }
        _first = now;
        return false;
    }

    /// <summary>Forgets a first press: Esc answered a card instead, or the turn ended.</summary>
    public void Reset() => _first = null;
}
