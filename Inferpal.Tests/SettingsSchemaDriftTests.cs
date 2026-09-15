using System.Xml.Linq;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The settings form is now declared once, in <see cref="SettingsSchema"/>, and served to the VS
/// Code panel over <c>settings/schema</c>. These tests are what makes that single source of truth
/// trustworthy: every declared field must be a real config key, and every label it points at must
/// exist in the resources.
/// </summary>
/// <remarks>
/// The drift this closes is not hypothetical: eight settings-window strings shipped untranslated
/// because they were added on the Visual Studio side and never propagated to the other front-end's
/// hand-written copy of the same form.
/// </remarks>
public class SettingsSchemaDriftTests
{
    /// <summary>Labels the adapter resolves itself rather than from the .resx.</summary>
    private static readonly string[] LocalLabels =
        [SettingsSchema.LocalLabelInlineDiff, SettingsSchema.LocalLabelTabTools];

    private static HashSet<string> ResourceNames()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var resx = Path.Combine(dir!, "Inferpal.Core", "Localization", "Strings.resx");
        return XDocument.Load(resx).Root!
            .Elements("data")
            .Select(e => e.Attribute("name")?.Value ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void TheSchemaCoversTheWholeForm()
    {
        // Guards the guard: an empty or gutted schema must not make the checks below vacuous.
        Assert.Equal(4, SettingsSchema.Tabs.Count);
        Assert.InRange(SettingsSchema.AllFields.Count(), 30, 200);
    }

    [Fact]
    public void EveryFieldMapsToAnInferpalConfigProperty()
    {
        var properties = typeof(InferpalConfig).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = SettingsSchema.AllFields
            .Select(f => f.Key)
            .Where(k => !properties.Contains(k))
            .Order()
            .ToList();

        Assert.True(unknown.Count == 0,
            "Settings fields with no matching InferpalConfig property (typo, or the property was "
            + "renamed/removed without updating the schema):\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public void EveryFieldKeyAppearsOnlyOnce()
    {
        var duplicates = SettingsSchema.AllFields
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order()
            .ToList();

        Assert.True(duplicates.Count == 0,
            "The same setting is editable twice (the two inputs would fight over the value):\n  "
            + string.Join("\n  ", duplicates));
    }

    [Fact]
    public void EveryLabelAndHintExistsInTheResources()
    {
        var resources = ResourceNames();

        var missing = SettingsSchema.AllFields
            .SelectMany(f => new[] { f.Label, f.Hint })
            .Concat(SettingsSchema.Tabs.Select(t => t.Title))
            .Concat(SettingsSchema.Tabs.SelectMany(t => t.Sections).SelectMany(sec =>
                new[] { sec.Title, sec.ToggleLabel, sec.ToggleHint }))
            .Where(n => !string.IsNullOrEmpty(n))
            .Where(n => !LocalLabels.Contains(n!))
            .Where(n => !resources.Contains(n!))
            .Distinct()
            .Order()
            .ToList();

        Assert.True(missing.Count == 0,
            "Settings labels/hints/titles with no matching resource (the panel would render the raw "
            + "key, and the .resx completeness test would never see them):\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// A numeric field's unit ("tokens", "turns", "msg"…) was an English literal, rendered as is by the VS Code
    /// panel in all ten languages. It now resolves like Label and Hint.
    /// </summary>
    [Fact]
    public void EveryUnitIsAResourceName()
    {
        var resources = ResourceNames();
        var units = SettingsSchema.AllFields.Select(f => f.Unit).Where(u => !string.IsNullOrEmpty(u)).ToList();

        // Witness: the form does carry units, otherwise the rule judges nothing.
        Assert.True(units.Count >= 10, $"only {units.Count} unit(s) read from the schema");

        var notResources = units.Where(u => !resources.Contains(u!)).Distinct().Order().ToList();
        Assert.True(notResources.Count == 0,
            "Settings units that are not resource names (the panel renders them verbatim, untranslated):\n  "
            + string.Join("\n  ", notResources));
    }

    [Fact]
    public void FimModeOptionTexts_MatchTheActualPresets()
    {
        // §27.6 - the labels promise "128 tok / 300 ms" literally: if the preset table of
        // FimContextBuilder moves, the form lies silently on both sides.
        var field = SettingsSchema.AllFields.Single(f => f.Key == "inlineCompletionMode");
        Assert.NotNull(field.Options);

        foreach (var opt in field.Options!)
        {
            var preset = FimContextBuilder.GetSettings(opt.Value);
            var delay  = preset.DebounceMs >= 1000
                ? $"{preset.DebounceMs / 1000} s"
                : $"{preset.DebounceMs} ms";
            Assert.Contains($"{preset.MaxTokens} tok", opt.Text);
            Assert.Contains(delay, opt.Text);
        }

        // GetSettings falls back to Default on an unknown code: pairwise-distinct presets prove
        // every option of the form is a real preset, not the fallback.
        var presets = field.Options!.Select(o => FimContextBuilder.GetSettings(o.Value)).ToList();
        Assert.Equal(presets.Count, presets.Distinct().Count());
    }

    [Fact]
    public void SelectFieldsDeclareTheirOptions()
    {
        var empty = SettingsSchema.AllFields
            .Where(f => f.Kind == SettingKind.Select)
            .Where(f => f.Options is null || f.Options.Count == 0)
            .Select(f => f.Key)
            .ToList();

        Assert.True(empty.Count == 0, "Select fields with no options:\n  " + string.Join("\n  ", empty));
    }

    /// <summary>
    /// A model field in the VS Code panel offers <b>every</b> model, whatever it already contains —
    /// like the Visual Studio window's combo box.
    /// </summary>
    /// <remarks>
    /// Reported from the UI on 2026-09-03: "the models offered are restricted, unlike Visual
    /// Studio". The field was an <c>&lt;input list="models"&gt;</c>, and Chromium filters a
    /// <c>&lt;datalist&gt;</c>'s options against what the input <b>already</b> contains: a field
    /// holding a model id offered nothing but itself, and no gesture showed the others.
    ///
    /// ⚠ What it cost to clear before accusing the rendering, because the symptom is
    /// indistinguishable from a poor backend: driving the host over JSON-RPC, <c>models/list</c>
    /// returns all 8 of the backend's models, and the extension's output log carries no
    /// <c>models/list failed</c>. The defect was entirely in the display. <b>A one-entry list does
    /// not read as a rendering bug</b> — it reads as a backend serving one model, and that is what
    /// kept it unreported.
    ///
    /// The rule is textual because a webview does not run here. It therefore targets the
    /// mechanism, not the look: no <c>list=</c> on a model field, a caret on each of them, and an
    /// opening that renders the list <b>unfiltered</b>.
    /// </remarks>
    [Fact]
    public void VsCodeModelFields_OfferEveryModel_NotOnlyTheOneAlreadyTyped()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        // Witness 1: the rule is only worth anything if model fields actually exist.
        var modelFields = SettingsSchema.AllFields.Count(f => f.Kind == SettingKind.Model);
        Assert.True(modelFields >= 4,
            $"Only {modelFields} model field(s) in the schema — the rule no longer guards anything.");

        var webview = Path.Combine(dir!, "vscode", "src", "webview", "settings.ts");
        Assert.True(File.Exists(webview), "vscode/src/webview/settings.ts has disappeared.");
        var source = File.ReadAllText(webview);

        // Witness 2: this really is the file rendering model fields, not an emptied namesake.
        Assert.Contains("field.kind === 'model'", source, StringComparison.Ordinal);

        // ⚠ The CONSTRUCTION, not the word. The first version banned "datalist" and "'list'", and
        // it came out red on the comment explaining the defect — the trap this repo has already
        // paid twice ("a pattern anchored on the word goes red on its own documentation", and its
        // mirror: it goes green on commented-out code).
        foreach (var mechanism in new[] { "createElement('datalist')", "setAttribute('list'", "list=\"models\"" })
            Assert.False(source.Contains(mechanism, StringComparison.Ordinal),
                $"vscode/src/webview/settings.ts rebuilds a datalist ({mechanism}): the browser will " +
                "filter its options against what the field already contains again, and a filled " +
                "field will offer nothing but itself.");

        // The caret is what replaces the datalist: without it there is no gesture at all to see
        // the list, which would be worse than the original defect.
        Assert.Contains("buildModelCaret", source, StringComparison.Ordinal);

        // And opening shows EVERYTHING: that is the property, the rest is presentation.
        Assert.Matches(new Regex(@"function openModelPopup[\s\S]{0,600}?renderModelPopup\(\)"), source);
    }

    /// <summary>
    /// The model lists of the Visual Studio window are all non-editable (<c>IsEditable="False"</c>): a model is
    /// picked, never typed. VS Code's model fields accepted any text — a typo saved a model the backend does not
    /// serve. They are read-only now; the optional roles keep their empty entry ("same as the chat model"), the
    /// leading <c>""</c> of Visual Studio.
    /// </summary>
    [Fact]
    public void VsCodeModelFields_AreReadOnly_LikeVisualStudio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        // Witness: the parity anchor — the Visual Studio window's lists are all non-editable.
        var xaml = File.ReadAllText(Path.Combine(dir!, "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));
        Assert.True(Regex.Matches(xaml, "IsEditable=\"False\"").Count >= 7,
            "The Visual Studio window's non-editable lists are no longer counted — the rule has lost its anchor.");
        Assert.DoesNotContain("IsEditable=\"True\"", xaml, StringComparison.Ordinal);

        var webview = Path.Combine(dir!, "vscode", "src", "webview", "settings.ts");
        Assert.True(File.Exists(webview), "vscode/src/webview/settings.ts has disappeared.");
        var source = NeutralizeTypeScriptComments(File.ReadAllText(webview));
        Assert.Matches(new Regex(@"field\.kind === 'model'[\s\S]{0,400}?\.readOnly = true"), source);
        Assert.False(source.Contains("addEventListener('input'", StringComparison.Ordinal),
            "A model field still filters as it is typed in: it accepts free text.");
        // The optional roles keep their empty entry, like the leading "" of AvailableOptionalModels.
        Assert.Contains("field.gate === 'roles'", source, StringComparison.Ordinal);

        // Read-only, the keyboard has nothing but the list to pick from: the arrow keys move through it and
        // Enter picks the highlighted row. Without that, the list opens from the keyboard and nothing can be picked.
        Assert.Matches(new Regex(@"addEventListener\('keydown'[\s\S]{0,900}?'ArrowUp'[\s\S]{0,400}?moveModelHighlight\(-1\)"), source);
        Assert.Matches(new Regex(@"addEventListener\('keydown'[\s\S]{0,1200}?pickModel\(modelChoices\[modelHighlight\]\)"), source);
    }

    /// <summary>
    /// VS Code's inline provider stays silent when inline completion is unchecked and waits for the delay of the
    /// chosen mode, like the Visual Studio leg. Its delay was a constant, and the panel showed settings that
    /// changed nothing.
    /// </summary>
    [Fact]
    public void VsCodeInlineCompletions_FollowInferpalsSettings()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var provider = Path.Combine(dir!, "vscode", "src", "inlineCompletions.ts");
        Assert.True(File.Exists(provider), "vscode/src/inlineCompletions.ts has disappeared.");
        var source = NeutralizeTypeScriptComments(File.ReadAllText(provider));
        // Witness: this really is the inline provider.
        Assert.Contains("provideInlineCompletionItems", source, StringComparison.Ordinal);

        Assert.Matches(new Regex(@"fimSettings\(\)"), source);
        Assert.Matches(new Regex(@"if \(!settings\?\.enabled\)[\s\S]{0,200}?delay\(settings\.debounceMs\)"), source);
        Assert.DoesNotContain("DEBOUNCE_MS", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A drop-down list of the VS Code panel whose saved value matches no option keeps it. With no option
    /// selected the browser shows the first one, and Save wrote it: an unknown FIM mode became "Fast" (the
    /// product applied "Default"), an unlisted language became "Auto".
    /// </summary>
    [Fact]
    public void VsCodeSelects_KeepAValueNoOptionLists()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var webview = Path.Combine(dir!, "vscode", "src", "webview", "settings.ts");
        Assert.True(File.Exists(webview), "vscode/src/webview/settings.ts has disappeared.");
        var source = NeutralizeTypeScriptComments(File.ReadAllText(webview));

        Assert.Matches(new Regex(@"function fillSelect\([\s\S]{0,1500}?if \(!match\)[\s\S]{0,400}?\.selected = true"), source);
        // Both lists — the language at the top and the schema fields — go through this filling.
        Assert.True(Regex.Matches(source, @"\bfillSelect\(").Count >= 3,
            "A drop-down list is still filled without going through fillSelect.");
        Assert.DoesNotContain(".selected = String(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The adapter tells the host whether a mirrored buffer has unsaved changes, and a save says it no
    /// longer does.
    /// </summary>
    /// <remarks>
    /// <c>read_file</c> served the mirrored buffer of every open document. After a tool wrote a file
    /// that was open and saved, the buffer still held the old text until the editor reloaded it from
    /// disk and the debounced change arrived — so an edit followed by a read in the same turn read the
    /// file as it was before the edit, and a document that is never reloaded (a watcher-excluded
    /// folder) stayed stale for as long as it was open. Only an unsaved buffer must win over the disk.
    /// </remarks>
    [Fact]
    public void EditorBridge_TellsTheHostWhetherABufferIsUnsaved()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        string Ts(string relative)
        {
            var path = Path.Combine(dir!, "vscode", "src", relative);
            Assert.True(File.Exists(path), $"vscode/src/{relative} has disappeared.");
            return NeutralizeTypeScriptComments(File.ReadAllText(path));
        }

        var source = Ts("editorBridge.ts");
        Assert.Matches(new Regex(@"didOpen\(\{[^}]*dirty: doc\.isDirty"), source);
        Assert.Matches(new Regex(@"didChange\(\{[^}]*dirty: e\.document\.isDirty"), source);
        Assert.Matches(
            new Regex(@"onDidSaveTextDocument\([\s\S]{0,400}?didChange\(\{[^}]*dirty: false"),
            source);
        Assert.Matches(new Regex(@"interface DocumentParams \{[^}]*dirty\?: boolean"), Ts("protocol.ts"));
    }

    /// <summary>
    /// Issue #8. "Use a separate model per role" unchecked promises the chat model everywhere — its
    /// tooltip says so. The panel only folded the fields away: the per-role models stayed in the
    /// configuration, the router kept using them, and the box came back checked at the next opening
    /// (it is derived from the filled-in fields).
    /// </summary>
    [Fact]
    public void VsCodePanel_SavingWithSeparateRoleModelsOff_ClearsTheRoleFields()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var webview = Path.Combine(dir!, "vscode", "src", "webview", "settings.ts");
        Assert.True(File.Exists(webview), "vscode/src/webview/settings.ts has disappeared.");
        var source = NeutralizeTypeScriptComments(File.ReadAllText(webview));

        var start = source.IndexOf("function onSave(", StringComparison.Ordinal);
        Assert.True(start >= 0, "webview/settings.ts has no onSave any more — the rule guards nothing.");
        var end  = source.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? source[start..] : source[start..end];

        // The fold is read from the schema (the "roles" gate), not from a list of keys copied here: a
        // role added to the schema must be covered without anyone thinking of it.
        Assert.True(body.Contains("gateOn.roles", StringComparison.Ordinal)
                    && body.Contains("'roles'", StringComparison.Ordinal),
            "onSave saves the per-role models even when \"Use a separate model per role\" is unchecked.");
    }

    /// <summary>
    /// A TypeScript source with its <b>comments neutralized</b> - replaced by spaces, length for
    /// length, newlines preserved, so offsets and line numbers stay exact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scan that reads raw text finds its patterns <b>inside comments</b>: a false red (the rule
    /// fires on the prose documenting the very defect it forbids) as much as a false green (a rule
    /// requiring a call to be present is satisfied by finding it commented out, i.e. disabled).
    /// </para>
    /// <para>
    /// There is no TypeScript syntax tree available here (Roslyn only reads C#), so this is a
    /// hand-written automaton - the shape this repository distrusts, hence its witness. The traps
    /// that matter come from real sources: a <b>regular expression</b> can contain a quote, a star
    /// and slashes - taking it for a string or a comment derails the rest of the line; and a
    /// <b>division</b> must not be taken for a regular expression.
    /// </para>
    /// <para>
    /// Same dividing line as on the PowerShell side: the neutralizer is wired in where a rule looks
    /// for a <b>shape of code</b>, never where it looks for a <b>structure</b> (a section header
    /// comment used as a landmark).
    /// </para>
    /// </remarks>
    internal static string NeutralizeTypeScriptComments(string text)
    {
        var b = text.ToCharArray();
        var n = b.Length;

        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < n; k++)
                if (b[k] != '\n' && b[k] != '\r') b[k] = ' ';
        }

        // A '/' opens a regular expression when what precedes it cannot be an operand - otherwise
        // it is a division. The look-behind reads the ALREADY neutralized buffer: a comment has
        // become spaces there, so it cannot hide the operator.
        bool RegexCanStartAt(int at)
        {
            var k = at - 1;
            while (k >= 0 && char.IsWhiteSpace(b[k])) k--;
            if (k < 0) return true;
            if ("(,=:[!&|?{};+-*%~^<>".IndexOf(b[k]) >= 0) return true;
            if (!char.IsLetter(b[k])) return false;

            var end = k + 1;                       // a keyword, not an identifier: `return /re/`
            while (k >= 0 && (char.IsLetterOrDigit(b[k]) || b[k] == '_')) k--;
            return new string(b, k + 1, end - k - 1) is "return" or "typeof" or "case" or "in"
                or "of" or "new" or "delete" or "void" or "instanceof" or "do" or "else"
                or "yield" or "await";
        }

        var templates = new Stack<int>();          // brace depth on entering each ${ }
        var depth = 0;
        var inTemplate = false;
        var i = 0;

        while (i < n)
        {
            if (inTemplate)                        // template literal: everything is text...
            {
                if (b[i] == '\\') { i += 2; continue; }
                if (b[i] == '$' && i + 1 < n && b[i + 1] == '{')   // ...except the interpolation
                {
                    templates.Push(depth); depth++; inTemplate = false; i += 2; continue;
                }
                if (b[i] == '`') { inTemplate = false; i++; continue; }
                i++; continue;
            }

            var c = b[i];

            if (c == '/' && i + 1 < n && b[i + 1] == '/')          // line comment
            {
                var close = text.IndexOf('\n', i);
                var stop  = close < 0 ? n : close;
                Blank(i, stop); i = stop; continue;
            }
            if (c == '/' && i + 1 < n && b[i + 1] == '*')          // block comment
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop  = close < 0 ? n : close + 2;
                Blank(i, stop); i = stop; continue;
            }
            if (c == '\'' || c == '"')                             // string: \ escapes, and it does
            {                                                      // not cross a newline - an
                var quote = c;                                     // unbalanced quote therefore
                i++;                                               // only eats its own line.
                while (i < n && b[i] != '\n')
                {
                    if (b[i] == '\\') { i += 2; continue; }
                    if (b[i] == quote) { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '`') { inTemplate = true; i++; continue; }
            if (c == '/' && RegexCanStartAt(i))                    // regular expression
            {
                i++;
                var inClass = false;                               // [ ... ]: a / is literal there
                while (i < n && b[i] != '\n')
                {
                    if (b[i] == '\\') { i += 2; continue; }
                    if (b[i] == '[') inClass = true;
                    else if (b[i] == ']') inClass = false;
                    else if (b[i] == '/' && !inClass) { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '{') { depth++; i++; continue; }
            if (c == '}')
            {
                if (templates.Count > 0 && depth == templates.Peek() + 1)
                {
                    templates.Pop(); depth--; inTemplate = true; i++; continue;
                }
                depth--; i++; continue;
            }
            i++;
        }
        return new string(b);
    }

    /// <summary>The witness of <see cref="NeutralizeTypeScriptComments"/>: both directions, the
    /// traps taken from real sources, and the offsets.</summary>
    /// <remarks>
    /// Without it the repair would be invisible: no source under <c>vscode\src</c> is in breach and
    /// every anchor of the rules above sits in real code, so they are green before and after. That
    /// is the failure mode this file exists to close.
    /// </remarks>
    [Fact]
    public void NeutralizeTypeScriptComments_KeepsCodeAndDropsComments()
    {
        var source = string.Join(Environment.NewLine,
        [
            "const url = 'http://x//y';                       // line: banned()",
            "const tpl = `a // b ${ real(1) } c`;",
            "const re  = s.replace(/[\\\\/:*?\"<>|]+/g, ' ');   // banned()",
            "const div = total / count;                       // banned()",
            "/* block:",
            "   banned(); */",
            "real(2);",
            "// real(3)",
            "function real(n: number) { return n; }",
        ]);

        var code = NeutralizeTypeScriptComments(source);

        // False RED: prose documenting a forbidden pattern no longer carries it. The three
        // end-of-line comments are the traps: a regular expression taken for a string (because of
        // the quote inside its character class), and a division taken for a regular expression,
        // would derail the rest of their line - and so let it through.
        Assert.DoesNotContain("banned", code, StringComparison.Ordinal);

        // False GREEN: a COMMENTED-OUT call no longer counts as present. The three real ones do.
        Assert.Equal(3, Regex.Matches(code, @"real\(").Count);

        // What is CODE, or TEXT inside a string or a template, survives intact.
        Assert.Contains("'http://x//y'", code, StringComparison.Ordinal);
        Assert.Contains("`a // b ${ real(1) } c`", code, StringComparison.Ordinal);
        Assert.Contains("replace(/[\\\\/:*?\"<>|]+/g", code, StringComparison.Ordinal);
        Assert.Contains("total / count", code, StringComparison.Ordinal);

        // Offsets are preserved: otherwise the line numbers in messages would lie.
        Assert.Equal(source.Length, code.Length);
        Assert.Equal(source.Count(ch => ch == '\n'), code.Count(ch => ch == '\n'));
    }

    /// <summary>
    /// A breakpoint the model sets is reported by the line it asked for first. The bridge took the first breakpoint
    /// within one line of the request, and VS Code lists the existing ones before the new one: with the user's own
    /// breakpoint on line 41, setting one on line 42 answered "Breakpoint set at …:41" — and a model that then clears
    /// what it set removes the user's.
    /// </summary>
    [Fact]
    public void VsCodeBreakpoint_IsReportedByTheLineAskedForFirst()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "vscode", "src", "debugBridge.ts");
        Assert.True(File.Exists(path), "vscode/src/debugBridge.ts has disappeared.");
        var bridge = NeutralizeTypeScriptComments(File.ReadAllText(path));
        var at = bridge.IndexOf("async addBreakpoint(", StringComparison.Ordinal);
        Assert.True(at >= 0, "addBreakpoint moved — the rule measures nothing.");
        var body = bridge[at..bridge.IndexOf("async removeBreakpoint(", at, StringComparison.Ordinal)];
        Assert.Contains("d.line === line", body, StringComparison.Ordinal);
    }

    /// <summary>Reads a TypeScript source of the extension, comments neutralized.</summary>
    private static string VsCodeSource(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "vscode", "src", relative);
        Assert.True(File.Exists(path), $"vscode/src/{relative} has disappeared.");
        return NeutralizeTypeScriptComments(File.ReadAllText(path));
    }

    /// <summary>
    /// Both captures of the bridge say <b>which frame</b> the locals came from.
    /// </summary>
    /// <remarks>
    /// They do not read the same one: <c>capture()</c> takes the top of the stack,
    /// <c>captureTest()</c> the first frame under the workspace root. The renderer, in turn,
    /// <b>hides</b> frames outside the workspace — so the frame printed first is often not the one
    /// the locals came from, and the label "Locals (current frame)" attributed a runtime frame's
    /// variables to the user's code. The field exists only to make that sentence true: a capture
    /// that forgets it falls back to "we do not know", which is to say to silence.
    ///
    /// The rule reads the <b>body</b> of each capture, not the file: <c>localsFrameId</c> written
    /// once somewhere would make both green.
    /// </remarks>
    [Fact]
    public void VsCodeDebugCaptures_SayWhichFrameTheLocalsCameFrom()
    {
        var bridge = VsCodeSource("debugBridge.ts");

        foreach (var (opening, closing) in new[]
                 {
                     ("private async capture(", "private async frames("),
                     ("async captureTest(",     "private async localsExpanded("),
                 })
        {
            var at = bridge.IndexOf(opening, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{opening} has disappeared from debugBridge.ts — the rule measures nothing.");
            var end = bridge.IndexOf(closing, at, StringComparison.Ordinal);
            Assert.True(end > at, $"{closing} no longer follows {opening} — the rule measures nothing.");

            Assert.Contains("localsFrameId", bridge[at..end], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The "the chat is holding the GPU" state is taken and released through a <b>single funnel</b>,
    /// and it is counted — the TypeScript twin of rule 29.
    /// </summary>
    /// <remarks>
    /// It was a boolean, and <b>three</b> requests raised it: <c>chat/send</c>,
    /// <c>command/slash</c> and <c>codeAction/run</c>. They overlap in ordinary use (a <c>/tdd</c>
    /// typed while an answer streams, a code action launched from the editor): the first
    /// <c>finally</c> cleared the flag while the other request still held the GPU lease, FIM stopped
    /// yielding, and its requests queued behind the busy GPU only to be dropped. A silent failure:
    /// nothing errors, completions are missing.
    ///
    /// The rule is about <b>shape</b>: the counter is only mutated inside the funnel, so a fourth
    /// GPU-holding request goes through it by construction.
    /// </remarks>
    [Fact]
    public void VsCodeHostClient_TakesAndReleasesTheChatBusyStateThroughOneFunnel()
    {
        var client = VsCodeSource("hostClient.ts");

        var open = client.IndexOf("private async whileChatBusy<T>(", StringComparison.Ordinal);
        Assert.True(open >= 0, "whileChatBusy has disappeared from hostClient.ts — the rule measures nothing.");
        var close = client.IndexOf("\n  }", open, StringComparison.Ordinal);
        Assert.True(close > open, "whileChatBusy has no readable end — the rule measures nothing.");

        // `this.` on purpose: the field declaration (`private chatBusyDepth = 0;`) is an
        // initialization, not a mutation — the first version of the rule went red on it.
        var mutations = Regex.Matches(client, @"this\.chatBusyDepth\s*(\+\+|--|=[^=])");
        Assert.True(mutations.Count >= 2,
            $"Only {mutations.Count} mutation(s) of the counter found — the rule measures nothing.");

        foreach (Match m in mutations)
            Assert.True(m.Index > open && m.Index < close,
                "hostClient.ts mutates the busy counter outside whileChatBusy (offset "
                + $"{m.Index}) — that is how a shared boolean used to clear itself while another "
                + "request still held the GPU lease. Go through the funnel.");

        // And the old shape must not come back through the back door.
        Assert.DoesNotMatch(new Regex(@"isChatBusy\s*=[^=]"), client);

        foreach (var request in new[] { "chatSend(", "commandSlash(", "codeActionRun(" })
        {
            var at = client.IndexOf("  " + request, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{request} has disappeared from hostClient.ts — the rule measures nothing.");
            var end = client.IndexOf("\n  }", at, StringComparison.Ordinal);
            Assert.True(end > at, $"{request} has no readable end — the rule measures nothing.");

            Assert.Contains("whileChatBusy", client[at..end], StringComparison.Ordinal);
        }
    }
    /// <summary>
    /// The VS Code manifest and the code agree on commands: every declared command has a handler,
    /// and every keybinding or menu entry points at a command that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing ties the two together at compile time: <c>package.json</c> is data, and
    /// <c>registerCommand</c> takes a string. The two halves fail differently and both in front of
    /// the user — a declared command with no handler shows up in the palette and answers
    /// <i>"command 'x' not found"</i> when picked; a keybinding or menu entry pointing at an unknown
    /// command does nothing at all.
    /// </para>
    /// <para>
    /// ⚠ The exemption is <b>derived</b>, not listed: VS Code makes <c>&lt;viewId&gt;.focus</c> for
    /// every contributed view, so a keybinding aimed at it is correct without appearing anywhere.
    /// The first draft counted it missing — that was the probe ignoring the host's rule, not the
    /// manifest lying.
    /// </para>
    /// <para>Measured at zero divergence on 2026-09-15: 10 declared, 10 registered, 8 menu entries.</para>
    /// </remarks>
    [Fact]
    public void EveryVsCodeCommand_IsDeclaredAndHandled()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "vscode", "package.json")))
                                   .RootElement.GetProperty("contributes");

        List<string> Strings(string section, string field) =>
            manifest.TryGetProperty(section, out var node) && node.ValueKind == JsonValueKind.Array
                ? [.. node.EnumerateArray()
                          .Where(e => e.TryGetProperty(field, out _))
                          .Select(e => e.GetProperty(field).GetString()!)]
                : [];

        var declared = Strings("commands", "command").ToHashSet(StringComparer.Ordinal);

        // All of the extension's TypeScript, comments neutralized: a commented-out registerCommand
        // is a handler that does not exist.
        var sources = Directory.EnumerateFiles(Path.Combine(dir!, "vscode", "src"), "*.ts",
                                               SearchOption.AllDirectories).ToList();
        Assert.True(sources.Count >= 10, $"Only {sources.Count} TypeScript source(s) read.");
        var code = string.Join("\n", sources.Select(f => NeutralizeTypeScriptComments(File.ReadAllText(f))));

        var registered = Regex.Matches(code, @"register(?:TextEditor)?Command\(\s*['""`]([^'""`]+)['""`]")
                              .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.True(declared.Count >= 5, $"Only {declared.Count} declared command(s): the manifest changed shape.");
        Assert.True(registered.Count >= 5, $"Only {registered.Count} registered command(s): the call shape changed.");

        Assert.True(declared.SetEquals(registered),
            "The manifest and the code disagree on commands — declared with no handler: "
            + $"[{string.Join(", ", declared.Except(registered).Order())}]; registered but not "
            + $"declared (invisible in the palette): [{string.Join(", ", registered.Except(declared).Order())}].");

        // VS Code makes `<viewId>.focus` for every contributed view.
        var viewFocus = manifest.TryGetProperty("views", out var views)
            ? views.EnumerateObject()
                   .SelectMany(c => c.Value.EnumerateArray())
                   .Select(v => v.GetProperty("id").GetString() + ".focus")
                   .ToHashSet(StringComparer.Ordinal)
            : [];

        var known = declared.Concat(registered).Concat(viewFocus).ToHashSet(StringComparer.Ordinal);

        var pointedAt = Strings("keybindings", "command")
            .Concat(manifest.TryGetProperty("menus", out var menus)
                        ? menus.EnumerateObject()
                               .SelectMany(g => g.Value.EnumerateArray())
                               .Where(e => e.TryGetProperty("command", out _))
                               .Select(e => e.GetProperty("command").GetString()!)
                        : [])
            .Where(c => !c.StartsWith('-'))     // a "-id" removes one of the host's keybindings
            .ToList();

        Assert.True(pointedAt.Count >= 5, $"Only {pointedAt.Count} entry point(s) read.");

        var ghosts = pointedAt.Where(c => !known.Contains(c)).Distinct().Order().ToList();
        Assert.True(ghosts.Count == 0,
            "A keybinding or menu entry points at a command that does not exist — it will do nothing "
            + "at all: " + string.Join(", ", ghosts));
    }
}
