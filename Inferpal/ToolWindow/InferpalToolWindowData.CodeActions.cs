using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Commands;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Code actions & bannière de build

    // ── Code-action helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the raw source code (no fences) of the active selection or full document,
    /// together with a display label suitable for use as an <see cref="AttachmentItem"/> chip.
    /// </summary>
    private async Task<(string RawCode, string FileName, string Label)> GetActiveCodeAsync(CancellationToken ct)
    {
        var view = await ResolveActiveViewAsync(ct);
        if (view is null) return (string.Empty, string.Empty, string.Empty);

        var fileName = Path.GetFileName(view.Document.Uri.LocalPath);
        var sel      = view.Selection;
        var rawCode  = !sel.IsEmpty
            ? sel.Extent.CopyToString()
            : view.Document.Text.CopyToString();
        var label    = !sel.IsEmpty
            ? $"Selection ({fileName})"
            : fileName;

        if (rawCode.Length > MaxCodeChars)
            rawCode = rawCode[..MaxCodeChars] + "\n…(truncated)";

        return (rawCode, fileName, label);
    }

    private static string DetectLanguage(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cs"     => "csharp",
            ".ts"     => "typescript",
            ".tsx"    => "typescript",
            ".js"     => "javascript",
            ".jsx"    => "javascript",
            ".py"     => "python",
            ".go"     => "go",
            ".java"   => "java",
            ".cpp"    => "cpp",
            ".c"      => "c",
            ".h"      => "cpp",
            ".hpp"    => "cpp",
            ".rs"     => "rust",
            ".fs"     => "fsharp",
            ".rb"     => "ruby",
            ".php"    => "php",
            ".swift"  => "swift",
            ".kt"     => "kotlin",
            ".razor"  => "razor",
            ".vue"    => "vue",
            _         => string.Empty,
        };

    /// <summary>
    /// Sends a code-action prompt with tools disabled (read-only response).
    /// Always uses the CodeActionsModel (or DefaultModel as fallback) as oneTimeModel,
    /// which triggers <see cref="EmptyToolRegistry"/> in <see cref="SendCoreAsync"/>.
    /// The file code is passed via <paramref name="attachments"/> (chip in the chat UI)
    /// rather than embedded in the prompt text.
    /// </summary>
    private async Task SendCodeActionAsync(string prompt, List<AttachmentItem> attachments, CancellationToken ct)
    {
        var model = string.IsNullOrEmpty(_config.CodeActionsModel)
            ? _config.DefaultModel
            : _config.CodeActionsModel;
        await SendCoreAsync(prompt, oneTimeModel: model, attachments: attachments, ct: ct, clearPrompt: true);
    }

    // ── Diagnostics fix ────────────────────────────────────────────────────────

    // Prompt formatting and diagnostic parsing live in FixPromptBuilder (unit-tested);
    // the VM only supplies the file reader.
    private static string BuildFixPrompt(string rawErrors) =>
        Services.CodeActions.FixPromptBuilder.Build(rawErrors, path =>
        {
            if (!File.Exists(path)) return null;
            try { return File.ReadAllText(path, System.Text.Encoding.UTF8); }
            catch { return null; }
        });

    // ── VS Build failure detection ────────────────────────────────────────────

    /// <summary>
    /// Called on a background thread by <see cref="VsBuildMonitor"/> when Visual Studio
    /// completes a solution build with at least one compilation error.
    /// Shows the dedicated "Build Failed" banner above the input card (LocalPilot-style),
    /// populated with the first error line.  The full error list is stored so that
    /// "Fix with AI" can pre-fill the prompt.
    /// </summary>
    private void OnVsBuildFailed(int errorCount, string errorLines)
    {
        // Show the banner regardless of Ollama connectivity.
        // If offline, clicking "Fix with AI" will surface the offline error naturally.

        // Extract first line for display; truncate if it is too wide for the banner.
        var firstError = Services.CodeActions.FixPromptBuilder.FirstErrorLine(errorLines);

        _buildFailedErrorLines = errorLines;

        Post(() =>
        {
            BuildFailedFirstError = firstError;
            HasBuildFailedBanner  = true;
        });
    }

    /// <summary>Closes the "Build Failed" banner without triggering a fix.</summary>
    private Task DismissBuildBannerAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() => HasBuildFailedBanner = false);

    /// <summary>
    /// Closes the banner and pre-fills <c>/fix-build</c> in the prompt so the user
    /// only needs to press Enter to start the automated build-fix loop.
    /// </summary>
    private Task FixBuildBannerAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() =>
        {
            HasBuildFailedBanner = false;
            Prompt = "/fix-build";
        });

    #endregion
}
