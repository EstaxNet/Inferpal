using System.Linq;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/diagnostics</c> — surfaces the in-memory <see cref="Diagnostics"/> ring so
/// swallowed best-effort errors are inspectable in the field without a debugger. Sub-commands:
/// <c>clear</c> (empty the ring), <c>on</c>/<c>off</c> (toggle file logging); no argument lists the
/// most recent entries. Pure and synchronous → unit-testable. Same pattern as
/// <see cref="SnippetsCommandHandler"/>.
/// </summary>
internal static class DiagnosticsCommandHandler
{
    private const int MaxShown = 30;

    /// <summary>Handles a <c>/diagnostics</c> invocation and returns the markdown to display.</summary>
    public static string Handle(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "clear":
                Diagnostics.Clear();
                return Strings.DiagnosticsCleared;

            case "on":
                Diagnostics.FileLoggingEnabled = true;
                return Strings.DiagnosticsFileOn;

            case "off":
                Diagnostics.FileLoggingEnabled = false;
                return Strings.DiagnosticsFileOff;

            default:
                var entries = Diagnostics.Snapshot();
                if (entries.Count == 0) return Strings.DiagnosticsEmpty;

                var sb = new StringBuilder(Strings.DiagnosticsHeader).Append('\n');
                foreach (var e in entries.Reverse().Take(MaxShown))   // most recent first
                    sb.Append("\n- `").Append(e.Timestamp.ToString("HH:mm:ss")).Append("` **")
                      .Append(e.Context).Append("** — ").Append(e.Detail);
                return sb.ToString();
        }
    }
}
