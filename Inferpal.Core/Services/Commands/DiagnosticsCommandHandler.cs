using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>Outcome of a <c>/diagnostics</c> invocation.</summary>
/// <param name="Message">Markdown to display in the chat.</param>
/// <param name="CopyToClipboard">When non-null, the caller must place this text on the clipboard.</param>
internal readonly record struct DiagnosticsCommandResult(string Message, string? CopyToClipboard = null);

/// <summary>
/// Everything the support bundle needs from the caller's world — passed in so the handler stays
/// pure and testable (ROADMAP §24).
/// </summary>
/// <param name="Config">Live configuration; secrets are redacted before anything is rendered.</param>
/// <param name="FrontEnd">Human-readable front-end label ("Visual Studio", "VS Code host").</param>
/// <param name="BackendStatus">The connection badge text the front-end already displays, if any.</param>
/// <param name="WorkspaceRoot">Workspace root, replaced by <c>&lt;workspace&gt;</c> in diagnostic details.</param>
internal sealed record DiagnosticsExportContext(
    InferpalConfig Config, string FrontEnd, string? BackendStatus = null, string? WorkspaceRoot = null);

/// <summary>
/// Execution logic for <c>/diagnostics</c> — surfaces the in-memory <see cref="Diagnostics"/> ring so
/// swallowed best-effort errors are inspectable in the field without a debugger. Sub-commands:
/// <c>clear</c> (empty the ring), <c>on</c>/<c>off</c> (toggle file logging), <c>export</c> (support
/// bundle, ROADMAP §24); no argument lists the most recent entries. Pure and synchronous →
/// unit-testable. Same pattern as <see cref="SnippetsCommandHandler"/>.
/// </summary>
/// <remarks>
/// <b>Why <c>export</c> exists.</b> Inferpal ships zero telemetry on principle, so the only field
/// signal the project ever gets is what a user pastes into a GitHub issue — and an issue used to
/// arrive with no version, OS, backend or context at all. The bundle is rendered <b>in the chat,
/// identically to what lands on the clipboard</b>: the user reads exactly what they are about to
/// send, and nothing leaves the machine on its own. Bundle body is deliberately English — its
/// audience is a public issue tracker, not the chat locale.
/// </remarks>
internal static class DiagnosticsCommandHandler
{
    private const int MaxShown = 30;

    /// <summary>Entries included in the support bundle (most recent first).</summary>
    private const int MaxExported = 20;

    /// <summary>Handles a <c>/diagnostics</c> invocation.</summary>
    /// <param name="export">Required for the <c>export</c> sub-command; unused otherwise.</param>
    public static DiagnosticsCommandResult Handle(string[] parts, DiagnosticsExportContext? export = null)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "clear":
                Diagnostics.Clear();
                return new(Strings.DiagnosticsCleared);

            case "on":
                Diagnostics.FileLoggingEnabled = true;
                return new(Strings.DiagnosticsFileOn);

            case "off":
                Diagnostics.FileLoggingEnabled = false;
                return new(Strings.DiagnosticsFileOff);

            case "export" when export is not null:
            {
                var bundle = BuildSupportBundle(export);
                return new(bundle + "\n\n" + Strings.DiagnosticsExported, CopyToClipboard: bundle);
            }

            default:
                var entries = Diagnostics.Snapshot();
                if (entries.Count == 0) return new(Strings.DiagnosticsEmpty);

                var sb = new StringBuilder(Strings.DiagnosticsHeader).Append('\n');
                foreach (var e in entries.Reverse().Take(MaxShown))   // most recent first
                    sb.Append("\n- `").Append(e.Timestamp.ToString("HH:mm:ss")).Append("` **")
                      .Append(e.Context).Append("** — ").Append(e.Detail);
                return new(sb.ToString());
        }
    }

    // ── Support bundle (§24) ────────────────────────────────────────────────────

    private static string BuildSupportBundle(DiagnosticsExportContext ctx)
    {
        var c  = ctx.Config;
        var sb = new StringBuilder();
        sb.Append("## Inferpal support bundle\n\n");

        var version = typeof(Diagnostics).Assembly.GetName().Version;
        sb.Append("- **Inferpal**: ").Append(version?.ToString(3) ?? "unknown")
          .Append(" — ").Append(ctx.FrontEnd).Append('\n');
        sb.Append("- **OS**: ").Append(Environment.OSVersion.VersionString)
          .Append(" (").Append(RuntimeInformation.OSArchitecture).Append(")\n");
        sb.Append("- **.NET**: ").Append(Environment.Version).Append('\n');
        sb.Append("- **Provider**: ").Append(c.Provider)
          .Append(" — ").Append(RedactEndpoint(c.BaseUrl)).Append('\n');
        sb.Append("- **API key**: ").Append(string.IsNullOrEmpty(c.ApiKey) ? "not set" : "set (redacted)").Append('\n');
        if (!string.IsNullOrEmpty(ctx.BackendStatus))
            sb.Append("- **Backend**: ").Append(ctx.BackendStatus).Append('\n');

        sb.Append("- **Models**: default `").Append(c.DefaultModel).Append('`')
          .Append(Role("agent", c.AgentModel)).Append(Role("utility", c.UtilityModel))
          .Append(Role("FIM", c.InlineCompletionModel)).Append(Role("embedding", c.RagEmbeddingModel))
          .Append('\n');
        sb.Append("- **Context window**: ").Append(c.ContextWindowSize)
          .Append(" tokens (keep ").Append(c.ContextWindowKeepTurns).Append(" turns)\n");

        sb.Append("- **Toggles**: ")
          .Append(Toggle("RAG", c.RagEnabled)).Append(Toggle("RAG auto-context", c.RagAutoContextEnabled))
          .Append(Toggle("FIM", c.InlineCompletionEnabled)).Append(Toggle("inline diff preview", c.InlineDiffPreviewEnabled))
          .Append(Toggle("MCP", c.McpEnabled)).Append(Toggle("LSP", c.LspEnabled))
          .Append(Toggle("router auto", c.ModelRouterAuto)).Append(Toggle("smart fix", c.SmartFixEnabled))
          .Append(Toggle("compaction", c.CompactionEnabled))
          // Called out loudly rather than folded into the on/off list: a report from a machine
          // with approvals muted reads very differently.
          .Append(c.SecurityAlertsDisabled ? " · **security alerts DISABLED**" : " · security alerts on")
          .Append('\n');

        var entries = Diagnostics.Snapshot();
        sb.Append("- **Recent diagnostics** (").Append(Math.Min(entries.Count, MaxExported))
          .Append(" of ").Append(entries.Count).Append("):");
        if (entries.Count == 0) sb.Append(" none");
        foreach (var e in entries.Reverse().Take(MaxExported))
            sb.Append("\n  - `").Append(e.Timestamp.ToString("HH:mm:ss")).Append("` **")
              .Append(e.Context).Append("** — ").Append(SanitizePaths(e.Detail, ctx.WorkspaceRoot));

        return sb.ToString();

        static string Role(string label, string model) =>
            $" · {label} {(string.IsNullOrEmpty(model) ? "(default)" : $"`{model}`")}";

        static string Toggle(string label, bool on) => $"{(label == "RAG" ? "" : " · ")}{label} {(on ? "on" : "off")}";
    }

    /// <summary>
    /// A loopback endpoint is shown as-is; anything else is reduced to "remote endpoint (redacted)"
    /// — a LAN hostname identifies the user's network and has no diagnostic value in a public issue.
    /// </summary>
    internal static string RedactEndpoint(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            return uri.IsLoopback ? $"`{uri.Scheme}://{uri.Host}:{uri.Port}`" : "remote endpoint (redacted)";
        }
        catch { return "invalid endpoint"; }
    }

    /// <summary>
    /// Diagnostic details often quote exception messages carrying absolute paths; the user profile
    /// collapses to <c>~</c> and the workspace root to <c>&lt;workspace&gt;</c> before export.
    /// </summary>
    internal static string SanitizePaths(string detail, string? workspaceRoot)
    {
        if (!string.IsNullOrEmpty(workspaceRoot))
            detail = detail.Replace(workspaceRoot, "<workspace>", StringComparison.OrdinalIgnoreCase);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            detail = detail.Replace(home, "~", StringComparison.OrdinalIgnoreCase);

        return detail;
    }
}
