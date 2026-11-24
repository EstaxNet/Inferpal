using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>run_notebook</c>: a Jupyter notebook executed from its first cell in the project's own Python, on a copy — the cells'
/// outputs and the first error's traceback read back, the person's file left as it is.
/// </summary>
/// <remarks>
/// ⚠ Executing a notebook runs the repository's code: asked once per workspace, like a build
/// (<see cref="Execution.WorkspaceCodeConsent"/>). The kernel works in the notebook's own folder (its relative paths hold);
/// <c>nbconvert</c> writes the executed copy into a folder of its own. Jupyter missing from the interpreter is said with the
/// command that installs it — never installed.
/// </remarks>
internal sealed class RunNotebookTool(Execution.WorkspaceCodeConsent? consent, Func<string?> getRoot,
                                      Editor.OpenDocumentOverlay? overlay = null) : ITool
{
    private readonly Execution.WorkspaceCodeConsent? _consent = consent;

    internal const int DefaultTimeout = 300, MinTimeout = 10, MaxTimeout = 1800, MaxReportChars = 7_000;

    public string Name => "run_notebook";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Runs;

    public string Description =>
        "Runs a Jupyter notebook (.ipynb) from its first cell, in the project's own Python environment, and returns each code " +
        "cell's output and the traceback of the first cell that fails. Runs on a copy: the notebook file is not changed. " +
        "Pass 'cell' to see one cell's output only.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path            = new { type = "string",  description = "The notebook (.ipynb)." },
            cell            = new { type = "string",  description = "Optional: the cell (id or number from 1) whose output to return; the notebook still runs from the start." },
            timeout_seconds = new { type = "integer", description = $"The longest the whole run may take, {MinTimeout}-{MaxTimeout} seconds (default {DefaultTimeout})." },
        },
        required = new[] { "path" },
    };

    private string? _offeredRoot;
    private bool _offered;
    private DateTime _offeredAt;

    /// <summary>Offered where the workspace holds a notebook.</summary>
    public bool IsOffered
    {
        get
        {
            var root = getRoot();
            if (string.IsNullOrEmpty(root)) return false;
            // Looked again after a while: a notebook created since is offered without reopening the workspace.
            if (!string.Equals(root, _offeredRoot, StringComparison.Ordinal) || (!_offered && DateTime.UtcNow - _offeredAt > TimeSpan.FromMinutes(1)))
            {
                _offered = WorkspaceScan.EnumerateFiles(root, "*.ipynb").Any();
                _offeredRoot = root;
                _offeredAt = DateTime.UtcNow;
            }
            return _offered;
        }
    }

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root = getRoot();
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        if (!path.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase)) return "Error: run_notebook runs .ipynb notebooks only.";
        if (!File.Exists(path)) return Execution.ToolFailure.Said(Strings.ToolFileNotFound(path));
        // The run reads the file on disk: unsaved cells in the editor would not be the code that runs.
        if (overlay is not null && overlay.HasUnsavedChanges(path))
            return $"'{path}' has unsaved changes in the editor, and the run reads the notebook on disk: what ran would not be " +
                   "what the user sees. Ask the user to save it, then run it again.";
        var (timeout, notice) = ClampedArgument.Read(args, "timeout_seconds", DefaultTimeout, MinTimeout, MaxTimeout);

        if (_consent is not null && await _consent.RefusalAsync(Name, ct) is { } refused) return refused;

        var folder = Path.GetDirectoryName(path)!;
        var python = RunTestsTool.ResolvePython(folder, root, OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath, File.Exists,
                                                Environment.GetEnvironmentVariable("VIRTUAL_ENV"));
        var dir = Directory.CreateTempSubdirectory("inferpal-notebook-").FullName;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = folder,
                ArgumentList = { "-m", "jupyter", "nbconvert", "--to", "notebook", "--execute", "--allow-errors",
                                 "--output-dir", dir, path },
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            ChildProcessResult run;
            try { run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(timeout), ct); }
            catch (System.ComponentModel.Win32Exception)
            {
                return ClampedArgument.Above(notice, $"{Path.GetFileName(path)} was not run: no Python was found to run it ({python}). " +
                                                     "The person can create the project's .venv, or install Python.");
            }
            var name = Path.GetFileName(path);
            var executed = Path.Combine(dir, name);
            if (run.TimedOut)
                return ClampedArgument.Above(notice, $"{name}: the run did not finish within {timeout} s and was stopped — a cell may wait or loop; " +
                                                     "pass a larger timeout_seconds, or run the cells that matter.");
            if (!File.Exists(executed))
                return ClampedArgument.Above(notice, NotRun(run.Combined, python, name));
            var report = Report(name, await File.ReadAllBytesAsync(executed, ct), args.Trimmed("cell"));
            return ClampedArgument.Above(notice, report + $"\n(Run in {python}, on a copy: {name} itself is unchanged.)");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temporary folder: best-effort cleanup */ }
        }
    }

    /// <summary>Why nothing ran: Jupyter or a kernel missing from the interpreter (said with the command that installs it, never
    /// run), else nbconvert's own words.</summary>
    internal static string NotRun(string output, string python, string name)
    {
        if (output.Contains("No module named jupyter", StringComparison.Ordinal) || output.Contains("No module named nbconvert", StringComparison.Ordinal)
            || output.Contains("is not a Jupyter command", StringComparison.Ordinal) || output.Contains("jupyter-nbconvert` not found", StringComparison.Ordinal))
            return $"{name} was not run: Jupyter (nbconvert) is not installed in {python}. The person can install it there with: " +
                   $"\"{python}\" -m pip install nbconvert ipykernel — do not install it yourself.";
        if (KernelMissing.Match(output) is { Success: true } kernel)
            return kernel.Groups["name"].Value.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                ? $"{name} was not run: the Python kernel is not installed in {python}. The person can install it with: " +
                  $"\"{python}\" -m pip install ipykernel — do not install it yourself."
                : $"{name} was not run: it names the kernel '{kernel.Groups["name"].Value}' (another language), which this Jupyter " +
                  "does not have — the person installs that language's kernel; do not install it yourself.";
        var text = output.Trim();
        return $"{name} was not run:\n{(text.Length > 2000 ? "…" + text[^2000..] : text)}";
    }

    private static readonly Regex KernelMissing = new(@"No such kernel named (?<name>[\w.-]+)", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// What the executed copy says: how many code cells ran, the first that failed with its traceback, and each code cell's
    /// output (or the one asked for). The first line is the verdict the step line reads (<c>RunSummary.StepStatus</c>).
    /// </summary>
    internal static string Report(string name, byte[] executed, string? only)
    {
        using var doc = JsonDocument.Parse(executed, new JsonDocumentOptions { MaxDepth = 64 });
        var cells = doc.RootElement.GetProperty("cells").EnumerateArray().ToList();
        var code = 0;
        var ran = 0;
        int? failed = null;
        var found = only is null;
        var failing = string.Empty;
        var body = new StringBuilder();
        var askedAt = only is null ? -1 : NotebookEdit.Find([.. cells.Select(c => c.TryGetProperty("id", out var e) ? e.AsText() : null)], only);
        only = only?.Trim().TrimStart('#');
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (!cell.TryGetProperty("cell_type", out var t) || t.AsText() != "code") continue;
            // An empty code cell is not executed (no count): not one that did not run.
            if (NotebookEdit.Source(System.Text.Json.Nodes.JsonNode.Parse(cell.GetRawText()) as System.Text.Json.Nodes.JsonObject).Trim().Length == 0) continue;
            code++;
            if (cell.TryGetProperty("execution_count", out var n) && n.ValueKind == JsonValueKind.Number) ran++;
            var number = i + 1;
            var id = cell.TryGetProperty("id", out var idElement) ? idElement.AsText() : null;
            var outputs = cell.TryGetProperty("outputs", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().ToList() : [];
            var error = outputs.FirstOrDefault(x => x.TryGetProperty("output_type", out var k) && k.AsText() == "error");
            if (failed is null && error.ValueKind == JsonValueKind.Object) failed = number;
            var asked = only is null || i == askedAt;
            found |= asked;
            if (!asked || (outputs.Count == 0 && error.ValueKind != JsonValueKind.Object)) continue;
            var block = new StringBuilder($"\nCell {number}{(id is null ? "" : $" (id {id})")}:\n");
            foreach (var output in outputs)
            {
                // An error is said by its traceback, which ends with its name and message: never twice.
                if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("traceback", out var tb) && tb.ValueKind == JsonValueKind.Array
                    && tb.GetArrayLength() > 0)
                    block.Append(ChildProcess.WithoutTerminalCodes(string.Join("\n", tb.EnumerateArray().Select(l => l.AsText()))).TrimEnd()).Append('\n');
                else block.Append(NotebookText.Output(output).TrimEnd()).Append('\n');
            }
            if (failed == number) failing = block.ToString();
            else body.Append(block);
        }
        if (!found) return $"{name}: no cell '{only}' in this notebook (an id, or a number from 1).";
        var verdict = $"{name}: {ran}/{code} code cells ran" + (failed is { } f ? $"; cell {f} failed" : string.Empty) +
                      (failed is { } g && ran > 0 ? $" (the cells after it ran too, on the state it left)" : string.Empty) + ".";
        // ⚠ The failing cell first, whole: cut after a chatty cell, its traceback was the part lost.
        var rest = body.ToString();
        var room = Math.Max(0, MaxReportChars - failing.Length);
        if (rest.Length > room)
            rest = rest[..room] + $"\n[output cut: {rest.Length - room} more characters — pass 'cell' for one cell's output]";
        var text = failing.Length + rest.Length == 0 ? "\n(No output" + (only is null ? "" : $" from cell '{only}'") + ".)" : failing + rest;
        return verdict + "\n" + text.TrimEnd();
    }
}
