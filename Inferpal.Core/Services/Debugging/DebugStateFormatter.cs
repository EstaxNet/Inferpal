using System.Text;

namespace Inferpal.Services.Debugging;

/// <summary>
/// Renders a <see cref="DebugStopState"/> as the markdown block the model reads. Pure and
/// testable: no debugger, no editor, no I/O.
/// </summary>
/// <remarks>
/// The frame filtering exists because of a measured fact, not a preference. In the §21 probe the
/// Node adapter answered a nine-frame stack for a program three calls deep — the rest was
/// <c>wrapModuleLoad</c>, <c>executeUserEntryPoint</c> and anonymous runtime frames. Handing those
/// to a model on a 16k window spends the scarce resource on noise, so frames outside the workspace
/// are dropped. If that would leave nothing, everything is kept: showing an empty stack to explain
/// a pause would be worse than showing runtime frames.
/// </remarks>
internal static class DebugStateFormatter
{
    internal const int MaxFrames = 12;
    internal const int MaxLocals = 25;
    internal const int MaxValueChars = 200;

    /// <summary>
    /// The frames worth showing: those whose file sits under <paramref name="rootDir"/>, or all of
    /// them when that leaves none (or when no root is known).
    /// </summary>
    internal static IReadOnlyList<DebugFrame> UserFrames(IReadOnlyList<DebugFrame> frames, string? rootDir)
    {
        if (frames.Count == 0 || string.IsNullOrWhiteSpace(rootDir)) return frames;

        var root = rootDir!.Replace('/', '\\').TrimEnd('\\');
        var kept = frames
            .Where(f => f.File is not null &&
                        f.File.Replace('/', '\\').StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return kept.Count > 0 ? kept : frames;
    }

    /// <summary>Model-facing English rendering of a paused debugger.</summary>
    internal static string Format(DebugStopState state, string? rootDir = null)
    {
        var sb = new StringBuilder("## Debugger paused\n");
        sb.Append("Stop reason: ").Append(state.Reason).Append('\n');

        if (!string.IsNullOrEmpty(state.Exception))
            sb.Append("\n### Exception\n").Append(state.Exception).Append('\n');

        var frames = UserFrames(state.Frames, rootDir);
        if (frames.Count > 0)
        {
            sb.Append("\n### Call stack (top first)\n");
            var hidden = state.Frames.Count - frames.Count;
            foreach (var f in frames.Take(MaxFrames))
            {
                sb.Append("- ").Append(f.Function);
                if (f.File is not null)
                {
                    sb.Append("  (").Append(f.File);
                    if (f.Line is not null) sb.Append(':').Append(f.Line);
                    sb.Append(')');
                }
                sb.Append('\n');
            }
            if (frames.Count > MaxFrames)
                sb.Append("- … ").Append(frames.Count - MaxFrames).Append(" more frame(s)\n");
            if (hidden > 0)
                sb.Append("- (").Append(hidden).Append(" runtime frame(s) outside the workspace hidden)\n");
        }

        if (state.Locals.Count > 0)
        {
            sb.Append("\n### Locals").Append(LocalsScope(state, frames)).Append('\n');
            foreach (var l in state.Locals.Take(MaxLocals))
                sb.Append("- `").Append(l.Name).Append("` (").Append(l.Type).Append(") = ")
                  .Append(Cap(l.Value)).Append('\n');
            if (state.Locals.Count > MaxLocals)
                sb.Append("- … ").Append(state.Locals.Count - MaxLocals).Append(" more local(s)\n");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The parenthetical that says which frame the locals belong to: "(current frame)" only when
    /// that frame is the one printed first, the frame named otherwise, and nothing at all when the
    /// producer did not say.
    /// </summary>
    /// <remarks>
    /// The old label claimed "current frame" unconditionally, next to a stack whose first line is
    /// the first frame <i>kept by <see cref="UserFrames"/></i>. On the measured Node stack — an
    /// exception thrown inside a library, three user frames below — the model was handed the
    /// runtime frame's variables as if they were the user frame's, which is the kind of wrong
    /// answer it has no way to notice. Naming the frame costs one line and removes the claim.
    /// </remarks>
    internal static string LocalsScope(DebugStopState state, IReadOnlyList<DebugFrame> shown)
    {
        if (state.LocalsFrameId is not { } id) return string.Empty;
        if (shown.Count > 0 && shown[0].Id == id) return " (current frame)";

        var frame = state.Frames.FirstOrDefault(f => f.Id == id);
        if (frame is null) return string.Empty;

        var where = frame.File is null ? string.Empty
                  : frame.Line is null ? $" at {frame.File}"
                  : $" at {frame.File}:{frame.Line}";
        return $" (frame: {frame.Function}{where} — not the frame listed first)";
    }

    /// <summary>Truncates an adapter-rendered value without pretending to understand it.</summary>
    internal static string Cap(string? value) =>
        value is null ? string.Empty :
        value.Length <= MaxValueChars ? value :
        value[..MaxValueChars] + "…";
}
