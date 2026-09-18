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
        var resx = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization", "Strings.resx");
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
            // .Display, not .Text: it is the RENDERED text that makes the promise. Now that these
            // three are translated, checking the English literal would let a translation lose the
            // figures without any test moving.
            Assert.Contains($"{preset.MaxTokens} tok", opt.Display);
            Assert.Contains(DelayText(preset.DebounceMs), opt.Display);
        }

        // GetSettings falls back to Default on an unknown code: pairwise-distinct presets prove
        // every option of the form is a real preset, not the fallback.
        var presets = field.Options!.Select(o => FimContextBuilder.GetSettings(o.Value)).ToList();
        Assert.Equal(presets.Count, presets.Distinct().Count());
    }

    /// <summary>
    /// The technical suffix of the three FIM modes survives in <b>all ten</b> languages. Translating
    /// "Fast" is the point; losing "128 tok · 300 ms" while doing it would be the same failure as
    /// before, the other way round — a form that lies about what it promises.
    /// </summary>
    [Fact]
    public void FimModeLabels_KeepTheirNumbers_InEveryLanguage()
    {
        var field = SettingsSchema.AllFields.Single(f => f.Key == "inlineCompletionMode");
        var dir   = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var files = Directory.GetFiles(dir, "Strings*.resx");

        // Witness: the ten files really are there. Zero files would green the rule for nothing.
        Assert.Equal(10, files.Length);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var values = XDocument.Load(file).Root!.Elements("data")
                .ToDictionary(e => e.Attribute("name")!.Value,
                              e => e.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

            foreach (var opt in field.Options!)
            {
                var key = "FimMode" + opt.Value;
                if (!values.TryGetValue(key, out var text))
                {
                    offenders.Add($"{Path.GetFileName(file)} : {key} absente");
                    continue;
                }
                var preset = FimContextBuilder.GetSettings(opt.Value);
                if (!text.Contains($"{preset.MaxTokens} tok", StringComparison.Ordinal)
                 || !text.Contains(DelayText(preset.DebounceMs), StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} : {key} = \"{text}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "FIM mode label that no longer carries its preset:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// The Visual Studio window redeclares none of the schema's option lists. It kept two copies of
    /// them, and one had <b>already drifted</b> — "OpenAI-compatible (generic)" on one side,
    /// "OpenAI-compatible" on the other — with no test able to see it.
    /// </summary>
    [Fact]
    public void TheVsWindow_DeclaresNoSecondCopyOfTheOptionLists()
    {
        // ⚠ Without the comments: the block that DOCUMENTS the drift quotes both labels in full.
        // One reader per language, never two — the C# one is CodeOnly.
        var source = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));

        // Witness: this really is the file that serves those lists.
        Assert.Contains("AvailableInlineModes", source, StringComparison.Ordinal);
        Assert.Contains("AvailableProviders",   source, StringComparison.Ordinal);

        // POSITIVE rule: both lists come from the schema.
        Assert.Contains("SettingsSchema.Providers", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSchema.FimModes",  source, StringComparison.Ordinal);

        // NEGATIVE rule, on the only labels that cannot be anything but a list entry. ⚠ Not on the
        // backend ones: "Ollama" and "LM Studio" legitimately appear elsewhere in this file (the
        // provider code, the capability hints), and a rule going red on them would be noise — hence
        // disarmed.
        foreach (var literal in SettingsSchema.FimModes.Select(o => o.Text))
            Assert.False(source.Contains(literal, StringComparison.Ordinal),
                $"The VS window rewrites the label '{literal}' instead of reading it from "
                + "SettingsSchema: the copy would stop being translated, and the two lists would "
                + "end up diverging the way the backend one already did.");
    }

    private static string DelayText(int debounceMs) =>
        debounceMs >= 1000 ? $"{debounceMs / 1000} s" : $"{debounceMs} ms";

    /// <summary>
    /// Clearing a numeric box restores the default — the same thing on <b>both</b> sides.
    /// </summary>
    /// <remarks>
    /// ⚠ the Visual Studio window applies this affordance
    /// (<c>SettingsFallback.For</c>: "empty" ⇒ the default, "unreadable" ⇒ what is configured) and
    /// the VS Code panel did <b>nothing</b> on a cleared box — it did not know the defaults. The same
    /// gesture, two results: two implementations of one rule. The host now serves them with the
    /// schema, and the panel applies them.
    /// </remarks>
    [Fact]
    public void ClearingANumericBox_RestoresTheDefault_InBothPanels()
    {
        // VS side: the shared decision exists and does tell the two states apart.
        Assert.Equal(7, SettingsFallback.For("", current: 3, whenCleared: 7));
        Assert.Equal(3, SettingsFallback.For("4o", current: 3, whenCleared: 7));

        // VS Code side: the panel receives the default, and applies it to a cleared box.
        var panel = NeutralizeTypeScriptComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "vscode", "src", "webview", "settings.ts")));
        // ⚠ BOTH numeric branches, by name. The first version of this rule looked for
        // "applyDefault(config, field": removing the call from the `int` branch left it GREEN,
        // because the `float` one was enough to satisfy it. A decoration, seen as such by sabotaging
        // it — that is what sabotage is for.
        Assert.Contains("applyDefault(config, field, parseInt)",   panel, StringComparison.Ordinal);
        Assert.Contains("applyDefault(config, field, parseFloat)", panel, StringComparison.Ordinal);
        Assert.Contains("field.defaultValue", panel, StringComparison.Ordinal);

        // And the host serves it, for numeric fields only.
        var host = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Host", "HostSettingsStrings.cs"));
        Assert.Contains("DefaultFor(f)", host, StringComparison.Ordinal);
        Assert.Contains("SettingKind.Int or SettingKind.Float", host, StringComparison.Ordinal);

        // ⚠ And the RPC really does return the count. It did NOT: `ConfigUpdate` had stayed `void` —
        // the DTO existed, the string was served, the panel displayed it, and the number was always
        // zero. The invisible half of a repair is the worst state: everything looks done. Lost while
        // restoring a sabotage backup over a real change, and caught only when porting the code to
        // the public clone.
        var server = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        Assert.Contains("public async Task<ConfigUpdateResult> ConfigUpdate(", server, StringComparison.Ordinal);
        Assert.Contains("return new ConfigUpdateResult(", server, StringComparison.Ordinal);
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
    /// Reported from the UI: "the models offered are restricted, unlike Visual
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
        // Witness 1: the rule is worth nothing unless model fields really remain to be guarded.
        var modelFields = SettingsSchema.AllFields.Count(f => f.Kind == SettingKind.Model);
        Assert.True(modelFields >= 4,
            $"Only {modelFields} model field(s) in the schema — the rule no longer guards anything.");

        var source = TsCode("webview/settings.ts");

        // Witness 2: this really is the file rendering model fields, not an emptied namesake.
        Assert.Contains("field.kind === 'model'", source, StringComparison.Ordinal);

        // ⚠ The WORD is forbidden again, and it is the neutralizer that allows it. The first version
        // of this rule banned "datalist", and it came out red on the comment explaining the defect;
        // it had therefore been narrowed to three spellings of the construct — a contortion that let
        // the fourth through. Now that the source is read without its comments (settings.ts carries
        // two that name the datalist), the rule can target the property: this element does not exist
        // in this file.
        foreach (var mechanism in new[] { "datalist", "setAttribute('list'", "list=\"models\"" })
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
        // Witness: the parity anchor — the Visual Studio window's lists are all non-editable.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsContent.xaml"));
        Assert.True(Regex.Matches(xaml, "IsEditable=\"False\"").Count >= 7,
            "The Visual Studio window's non-editable lists no longer count — the rule has lost its anchor.");
        Assert.DoesNotContain("IsEditable=\"True\"", xaml, StringComparison.Ordinal);

        var source = TsCode("webview/settings.ts");
        Assert.Matches(new Regex(@"field\.kind === 'model'[\s\S]{0,400}?\.readOnly = true"), source);
        Assert.False(source.Contains("addEventListener('input'", StringComparison.Ordinal),
            "A model field still filters as you type: it therefore accepts free text.");
        // Optional roles keep their empty entry, like the leading "" of AvailableOptionalModels.
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
        var source = TsCode("inlineCompletions.ts");
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
        var source = TsCode("webview/settings.ts");

        Assert.Matches(new Regex(@"function fillSelect\([\s\S]{0,1500}?if \(!match\)[\s\S]{0,400}?\.selected = true"), source);
        // Both lists — the language at the top and the schema fields — go through this filling.
        Assert.True(Regex.Matches(source, @"\bfillSelect\(").Count >= 3,
            "A drop-down list is still filled without going through fillSelect.");
        Assert.DoesNotContain(".selected = String(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document the editor stops mirroring is REMOVED from the host's overlay, never simply left
    /// behind.
    /// </summary>
    /// <remarks>
    /// <c>OpenDocumentOverlay</c> wins over the disk <b>unconditionally</b>: <c>ReadFileTool</c>
    /// returns the buffer and never opens the file when an entry exists. The VS Code adapter stops
    /// mirroring a document above a 1 MB ceiling — and it then left the change handler <b>without
    /// sending <c>didClose</c></b>. A file opened under the ceiling and then grown above it (a
    /// paste, a log that keeps growing) therefore left the host serving the model its last version
    /// under 1 MB, for as long as it stayed open, without a word — and the header comment promised
    /// the opposite ("the host reads them from disk instead"), which is only true if the overlay
    /// carries no entry.
    ///
    /// The rule is textual because there is no TypeScript harness here. It therefore targets both
    /// halves of the mechanism: tracking what is mirrored, and the <c>didClose</c> that cancels it.
    /// </remarks>
    [Fact]
    public void EditorBridge_DropsTheOverlayEntry_WhenADocumentStopsBeingMirrored()
    {
        var source = TsCode("editorBridge.ts");

        // Witness: this really is the file that mirrors documents, not an emptied namesake.
        foreach (var anchor in new[] { "onDidChangeTextDocument", "MAX_MIRRORED_BYTES", "didChange(" })
            Assert.True(source.Contains(anchor, StringComparison.Ordinal),
                $"editorBridge.ts no longer carries '{anchor}': the rule guards nothing any more.");

        // Tracking what is mirrored, and the gesture that cancels it.
        Assert.Contains("this.mirrored", source, StringComparison.Ordinal);
        Assert.Contains("dropMirror", source, StringComparison.Ordinal);

        // ⚠ The CALL, not the word: dropMirror must really notify the host, otherwise the entry
        // stays and the model re-reads a stale file — the original defect, identically.
        Assert.Matches(
            new Regex(@"private dropMirror[\s\S]{0,500}?didClose\("),
            source);

        // ⚠ BOTH branches that stop mirroring must call it, and the rule must tell them apart: the
        // first version of this test looked for "onDidChangeTextDocument … dropMirror" and stayed
        // GREEN when the original defect was put back (a bare return on the pre-filter), because it
        // found the other branch's call 900 characters further on. Verified red since, on that exact
        // sabotage.
        Assert.Matches(
            new Regex(@"if \(!couldBeMirrorable\([^)]*\)\) {[\s\S]{0,160}?dropMirror\("),
            source);
        Assert.Matches(
            new Regex(@"Buffer.byteLength[\s\S]{0,200}?MAX_MIRRORED_BYTES[\s\S]{0,160}?dropMirror\("),
            source);
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
        var source = TsCode("editorBridge.ts");

        Assert.Matches(new Regex(@"didOpen\(\{[^}]*dirty: doc\.isDirty"), source);
        Assert.Matches(new Regex(@"didChange\(\{[^}]*dirty: e\.document\.isDirty"), source);
        Assert.Matches(
            new Regex(@"onDidSaveTextDocument\([\s\S]{0,400}?didChange\(\{[^}]*dirty: false"),
            source);
        Assert.Matches(new Regex(@"interface DocumentParams \{[^}]*dirty\?: boolean"), TsCode("protocol.ts"));
    }

    /// <summary>
    /// No path comparator in the VS Code adapter folds case <b>unconditionally</b>.
    /// </summary>
    /// <remarks>
    /// Case folding is a property of the <b>file system</b>, not of the process: Windows and macOS
    /// (APFS by default) fold, Linux does not — and the extension is published as linux-x64.
    /// <c>OpenDocumentOverlay</c>, on the Core side, has chosen its comparator that way since §23;
    /// both comparators in <c>debugBridge.ts</c> folded everywhere, which made <c>a.cs</c> and
    /// <c>A.cs</c> the <b>same</b> breakpoint under Linux — removing one removed the other, and the
    /// listing handed the model the wrong file. The doctrine existed, this file did not have it.
    ///
    /// The rule carries the <b>shape</b> and not today's two functions: what is forbidden is the
    /// normalization idiom (replacing backslashes) followed by an unconditional
    /// <c>toLowerCase()</c>, so a third comparator would inherit it.
    /// </remarks>
    [Fact]
    public void VsCodeAdapter_NeverFoldsPathCaseUnconditionally()
    {
        var root = Path.Combine(RepoRoot(), "vscode", "src");
        var sources = Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories).ToList();

        // Witness: a renamed folder or a broken glob would green the rule while reading nothing.
        Assert.True(sources.Count >= 10,
            $"Only {sources.Count} TypeScript source(s) read under vscode/src — the rule scans "
            + "nothing any more.");

        // The idiom: normalize the separators, then fold case, on the same expression.
        var folding = new Regex(@"replace\(/\\\\/g[^)]*\)[^;\r\n]*\.toLowerCase\(\)");
        var offenders = sources
            .Where(f => folding.IsMatch(NeutralizeTypeScriptComments(File.ReadAllText(f))))
            .Select(f => Path.GetFileName(f))
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0,
            "Path comparator folding case with no platform condition: "
            + string.Join(", ", offenders)
            + " — under Linux, a.cs and A.cs become the same file. Go through a helper guarded by "
            + "process.platform, the way OpenDocumentOverlay does on the Core side.");

        // And the guard must exist somewhere, otherwise the rule above is green because nobody
        // compares paths any more.
        var bridge = TsCode("debugBridge.ts");
        Assert.Contains("process.platform", bridge, StringComparison.Ordinal);
        Assert.Contains("normalizePath", bridge, StringComparison.Ordinal);
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
        var bridge = TsCode("debugBridge.ts");
        var at = bridge.IndexOf("async addBreakpoint(", StringComparison.Ordinal);
        Assert.True(at >= 0, "addBreakpoint moved — the rule measures nothing.");
        var body = bridge[at..bridge.IndexOf("async removeBreakpoint(", at, StringComparison.Ordinal)];
        Assert.Contains("d.line === line", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The "chat is holding the GPU" state is taken and returned through a <b>single funnel</b>, and
    /// it is counted — the TypeScript twin of rule 29.
    /// </summary>
    /// <remarks>
    /// It was a boolean, and <b>three</b> requests raised it: <c>chat/send</c>,
    /// <c>command/slash</c> and <c>codeAction/run</c>. They overlap in ordinary use (a <c>/tdd</c>
    /// typed while an answer is streaming, a code action started from the editor): the first
    /// <c>finally</c> reset the flag to false while the other request still held the GPU lease, FIM
    /// stopped yielding, and its requests went to queue behind the busy GPU only to be thrown away.
    /// A silent failure: nothing fails, completions are missing.
    ///
    /// The rule carries the <b>shape</b>: the counter is mutated only in the funnel, and a fourth
    /// request holding the GPU would go through it by construction.
    /// </remarks>
    [Fact]
    public void VsCodeHostClient_TakesAndReleasesTheChatBusyStateThroughOneFunnel()
    {
        var client = TsCode("hostClient.ts");

        var open = client.IndexOf("private async whileChatBusy<T>(", StringComparison.Ordinal);
        Assert.True(open >= 0, "whileChatBusy has disappeared from hostClient.ts — the rule measures nothing.");
        var close = client.IndexOf("\n  }", open, StringComparison.Ordinal);
        Assert.True(close > open, "whileChatBusy has no readable end — the rule measures nothing.");

        // Witness: with no mutation found, "no mutation outside the funnel" would be true of a file
        // that counts nothing at all any more.
        // `this.` on purpose: the field's declaration (`private chatBusyDepth = 0;`) is an
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
    /// The bridge's two captures say <b>which frame</b> the locals come from.
    /// </summary>
    /// <remarks>
    /// They do not read the same one: <c>capture()</c> takes the top of the stack,
    /// <c>captureTest()</c> the first frame below the workspace root. The rendering, however,
    /// <b>hides</b> the frames outside the workspace — so the frame printed first is often not the
    /// one the locals come from, and the "Locals (current frame)" label attributed runtime-frame
    /// variables to the user's code. The field has no other purpose than making that sentence true:
    /// a capture that forgets it falls back to "we do not know", that is, to silence.
    ///
    /// The rule reads the <b>body</b> of each capture, not the file: <c>localsFrameId</c> written
    /// once somewhere would green both.
    /// </remarks>
    [Fact]
    public void VsCodeDebugCaptures_SayWhichFrameTheLocalsCameFrom()
    {
        var bridge = TsCode("debugBridge.ts");

        foreach (var (name, opening, closing) in new[]
                 {
                     ("capture()",     "private async capture(",  "private async frames("),
                     ("captureTest()", "async captureTest(",      "private async localsExpanded("),
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
    /// The three model lists go through <c>SelectionPreservingList</c>, each with the values its
    /// bound properties carry — the chat model included.
    /// </summary>
    /// <remarks>
    /// A Selector writes null into its bound property when the selected item leaves the collection,
    /// through <c>Clear()</c> as through the last <c>RemoveAt</c>, and under Remote UI that null
    /// comes back after any re-add: "remove then add back" protects nothing. The guarantee rests on
    /// a held value never being removed. The funnel does that (checked on its events in
    /// <c>SelectionPreservingListTests</c>); this rule holds that the window goes through it, with
    /// the right values, and removes nothing itself on the side.
    ///
    /// A textual rule: the Remote UI window does not instantiate in a test.
    /// </remarks>
    [Fact]
    public void VsSettings_ModelListsNeverDropTheValueTheirDropdownsHold()
    {
        // The repository's C# reader, ConventionCoverageTests': a COMMENTED-OUT call does not count.
        var source = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));

        (string List, string[] Held)[] lists =
        [
            ("AvailableModels",          ["current"]),
            ("AvailableOptionalModels",  ["CodeActionsModel", "InlineCompletionModel", "InlineEditModel", "AgentModel", "UtilityModel"]),
            ("AvailableEmbeddingModels", ["RagEmbeddingModel"]),
        ];

        foreach (var (list, held) in lists)
        {
            var call = Regex.Match(source, @"SelectionPreservingList\.Sync\(\s*" + list + @"\b[^;]*;");
            Assert.True(call.Success, $"{list} is no longer updated through SelectionPreservingList.");

            foreach (var value in held)
                Assert.True(Regex.IsMatch(call.Value, @"\b" + value + @"\b"),
                    $"{list} is synced without {value}: its selected value can leave the list.");

            Assert.False(Regex.IsMatch(source, @"\b" + list + @"\.(RemoveAt|Remove|Clear)\("),
                $"{list} removes items outside SelectionPreservingList.");
        }
    }

    /// <summary>
    /// A numeric entry that was not kept is <b>named</b> to the user, on both sides — and the VS
    /// Code panel reads strictly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dams's arbitration: we save, and we name the ignored fields. Refusing the whole
    /// form would also cancel the other valid changes of the same save; staying silent was the
    /// original defect — the Visual Studio window announced "saved" having restored a factory
    /// default, the VS Code panel having kept everything.
    /// </para>
    /// <para>
    /// ⚠ On the VS Code side there were two defects, not one: <c>parseInt('12abc')</c> is <b>12</b>,
    /// so the permissive read did not ignore the faulty entry — it wrote a <i>truncation</i> the
    /// user had not typed. The rule therefore forbids the permissive shape itself, not only the
    /// missing message.
    /// </para>
    /// <para>A textual rule: neither the webview nor the Remote UI window runs here.</para>
    /// </remarks>
    /// <summary>
    /// The reasoning preview crosses the adapter — it is not dropped there.
    /// </summary>
    /// <remarks>
    /// `chat/thinking` carried the text and the adapter ignored it (<c>onThinking: () =&gt;
    /// …</c>, with no parameter): a reasoning model's thinking phase, which can take most of a turn,
    /// showed nothing but a frozen status. ⚠ The rule carries the <b>parameter</b>, because its
    /// absence was the defect — a callback that takes nothing can pass nothing on, and it reads like
    /// a callback that works.
    /// </remarks>
    [Fact]
    public void ReasoningPreview_ReachesTheWebview_InsteadOfBeingDropped()
    {
        var provider = TsCode("chatViewProvider.ts");
        var webview  = TsCode("webview/main.ts");

        // Witness: the channel still exists on both sides.
        Assert.Contains("onThinking:", provider, StringComparison.Ordinal);
        Assert.Contains("case 'thinking':", webview, StringComparison.Ordinal);

        // The adapter TAKES the text and passes it on.
        Assert.Matches(new Regex(@"onThinking:\s*\(\s*text\s*\)\s*=>[\s\S]{0,120}?type: 'thinking'[\s\S]{0,40}?text"), provider);

        // And the webview displays it when there is one, keeping its generic fallback.
        Assert.Matches(new Regex(@"case 'thinking':[\s\S]{0,200}?msg\.text[\s\S]{0,80}?t\('thinking'\)"), webview);
    }

    /// <summary>
    /// A backend outage is <b>announced</b> in the VS Code thread, and it is the Core that decides
    /// when.
    /// </summary>
    /// <remarks>
    /// Losing the backend mid-session changed nothing there but the colour of a badge. A badge says
    /// "this is how it is now"; it does not say "this has just gone down" — and that is the sentence
    /// the Visual Studio window has always put in the conversation. ⚠ The rule requires the adapter
    /// to <b>render</b> the host's verdict, not to write a second state machine: "first successful
    /// check is silent, one message per edge" is exactly the kind of arithmetic that diverges when
    /// copied.
    /// </remarks>
    [Fact]
    public void BackendOutage_IsAnnouncedInTheThread_FromTheCoreVerdict()
    {
        var provider = TsCode("chatViewProvider.ts");

        // Witness: the connection heartbeat still exists in this file.
        Assert.Contains("host.backendStatus()", provider, StringComparison.Ordinal);

        // The verdict comes from the host, and it is rendered in the thread.
        Assert.Matches(new Regex(@"edgeNotice\s*=\s*s\.edgeNotice"), provider);
        Assert.Matches(new Regex(@"if \(edgeNotice\)[\s\S]{0,200}?this\.append\("), provider);

        // ⚠ And no second state machine here: the "which edge" decision belongs to the Core.
        foreach (var reinvented in new[] { "previouslyConnected", "wasConnected", "firstCheck" })
            Assert.False(provider.Contains(reinvented, StringComparison.OrdinalIgnoreCase),
                $"chatViewProvider.ts carries '{reinvented}': the edge decision is being copied, "
                + "so it will diverge. It lives in ConnectionStatusPresenter.");
    }

    /// <summary>
    /// Conversation export is rendered by the <b>Core</b>, once, for both editors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was written twice: <c>ConversationExporter</c> on the Core side for Visual Studio, and
    /// eleven lines of TypeScript for VS Code. The copy lost <b>all</b> of the statistics header
    /// (model, turns, tool calls, tokens, date, duration) and ignored the <c>.txt</c> filter its own
    /// save box offered: choosing *Text* wrote Markdown.
    /// </para>
    /// <para>
    /// ⚠ The rule targets <b>both</b> halves: that the document comes from the host, and that the
    /// format comes from the file the user named. An export going through the host but always
    /// sending <c>asPlainText: false</c> would leave the filter inert, which was the defect.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConversationExport_IsRenderedByTheCore_ForBothEditors()
    {
        var provider = TsCode("chatViewProvider.ts");

        // Witness: the export command still exists in this file.
        Assert.Contains("async exportCommand()", provider, StringComparison.Ordinal);

        // The document comes from the Core, through the host.
        Assert.Contains("host.chatExport({", provider, StringComparison.Ordinal);

        // And the format comes from the extension the user chose, not from a constant.
        Assert.Matches(new Regex(@"asPlainText:\s*target\.fsPath[^,]*'\.txt'"), provider);

        // ⚠ No second exporter anywhere in the extension: the duplication was the defect, not the
        // place where it lived.
        var root  = Path.Combine(RepoRoot(), "vscode", "src");
        var files = Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 5, $"Only {files.Count} TypeScript source(s) found under "
            + "vscode/src — the rule sweeps nothing any more.");

        var offenders = files
            .Where(f => NeutralizeTypeScriptComments(File.ReadAllText(f))
                        .Contains("renderExport", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();
        Assert.True(offenders.Count == 0,
            "A conversation exporter still lives in the extension: "
            + string.Join(", ", offenders)
            + ". Rendering belongs to the Core — with two copies, this is the one that will be "
            + "forgotten when a fix comes.");
    }

    /// <summary>
    /// The VS Code panel's two buttons act on what the user HAS IN FRONT OF THEM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The webview already sent the typed URL with <c>testConnection</c>, the message type declared
    /// it — and the handler did not read it: it probed the <b>saved</b> configuration. The panel
    /// could therefore display "Connected" about another URL, and the case that fools you most is
    /// the most ordinary one: the old one works, the new one is wrong. The comment covering the gap
    /// invoked a symmetry with the Visual Studio window that does not exist.
    /// </para>
    /// <para>
    /// ⚠ The rule targets <b>both</b> buttons. Closing "Test" alone would have left the class alive —
    /// <i>the panel acts on what is saved while the user looks at what they typed</i> — of which the
    /// models ↻ is the other half.
    /// </para>
    /// <para>
    /// ⚠ A field declared and never read is worse than a missing field: it makes you believe the
    /// information travels. That is why the rule carries the <b>read</b> (<c>msg.baseUrl</c> passed
    /// to the call), never the presence of the field.
    /// </para>
    /// </remarks>
    [Fact]
    public void SettingsPanel_ActsOnTheFormValues_NotOnTheSavedConfiguration()
    {
        var panel = TsCode("settingsPanel.ts");

        // Witness: both buttons still exist in this file.
        foreach (var anchor in new[] { "case 'testConnection':", "case 'refreshModels':" })
            Assert.True(panel.Contains(anchor, StringComparison.Ordinal),
                $"settingsPanel.ts no longer carries '{anchor}': the rule guards nothing any more.");

        // "Test" probes the form's URL WITH the form's key, not the session's. ⚠ The rule required
        // `connectionCheck(msg.baseUrl)` alone — it therefore froze the other half of the defect: a
        // new URL probed with the old key answered "unreachable".
        Assert.Matches(new Regex(@"connectionCheck\(\s*msg\.baseUrl\s*,\s*msg\.apiKey\s*\)"), panel);

        // ↻ lists the models of the form's backend — all three values, because a correct URL with
        // the old provider or the old key still queries something else.
        var refresh = Regex.Match(panel, @"modelsList\(\s*\{[\s\S]{0,200}?\}\s*\)");
        Assert.True(refresh.Success, "settingsPanel.ts calls modelsList without passing it the "
            + "form values: the ↻ button then lists the models of the SAVED URL.");
        foreach (var field in new[] { "baseUrl", "provider", "apiKey" })
            Assert.True(refresh.Value.Contains($"msg.{field}", StringComparison.Ordinal),
                $"the model refresh does not pass msg.{field} on.");

        // And the result NAMES the backend found, like the Visual Studio window.
        Assert.Contains("provider: result.provider", panel, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #8. "Use a separate model per role" unchecked promises the chat model everywhere — its
    /// tooltip says so. The panel only collapsed the fields: the per-role models stayed in the
    /// configuration, the router kept using them, and the box came back checked on the next opening
    /// (it is inferred from the filled fields).
    /// </summary>
    [Fact]
    public void VsCodePanel_SavingWithSeparateRoleModelsOff_ClearsTheRoleFields()
    {
        var source = TsCode("webview/settings.ts");

        var start = source.IndexOf("function onSave(", StringComparison.Ordinal);
        Assert.True(start >= 0, "webview/settings.ts has no onSave any more — the rule guards nothing.");
        var end  = source.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? source[start..] : source[start..end];

        // The fallback is read from the schema (the "roles" gate), not from a list of keys copied
        // here: a role added to the schema must be covered without anyone thinking about it.
        Assert.True(body.Contains("gateOn.roles", StringComparison.Ordinal)
                    && body.Contains("'roles'", StringComparison.Ordinal),
            "onSave saves the per-role models even when 'Use a separate model per role' is unchecked.");
    }

    [Fact]
    public void BothPanels_NameTheNumericFieldsTheyCouldNotRead()
    {
        var webview = TsCode("webview/settings.ts");

        // Witness: the panel really does read numeric boxes.
        foreach (var anchor in new[] { "case 'int':", "case 'float':" })
            Assert.True(webview.Contains(anchor, StringComparison.Ordinal),
                $"webview/settings.ts no longer carries '{anchor}': the rule guards nothing any more.");

        // The permissive read is forbidden: it truncates instead of ignoring.
        foreach (var lax in new[] { "parseInt(input.value", "parseFloat(input.value" })
            Assert.False(webview.Contains(lax, StringComparison.Ordinal),
                $"webview/settings.ts reads a numeric box with no guard ({lax}): '12abc' is worth "
                + "12 there, so a value the user never typed is written without a word.");

        // Both branches name what they did not read, and the status line renders it.
        Assert.Equal(2, Regex.Matches(webview, @"ignored\.push\(").Count);
        Assert.Matches(new Regex(@"case 'saveDone':[\s\S]{0,240}?savedStatus\(\)"), webview);
        Assert.Contains("SettingsFieldsIgnored", webview, StringComparison.Ordinal);

        // ── Visual Studio side: every fallback carries its report ─────────────
        var vs = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));

        var fallbacks = Regex.Matches(vs, @"SettingsFallback\.For\(");
        // Witness: the nine numeric boxes all go through this fallback — seven through ReadInt (a
        // single site in the text) and two inline, for their own culture and their own guard.
        Assert.True(fallbacks.Count >= 3,
            $"Only {fallbacks.Count} fallback(s) found in InferpalSettingsData — the rule "
            + "measures nothing any more.");

        // ⚠ The PROPERTY, not today's sites: a fallback is an entry that did not take, hence a field
        // to name. A tenth numeric box added without its Note() will come out red here.
        foreach (Match f in fallbacks)
        {
            var from = Math.Max(0, f.Index - 500);
            var before = vs[from..f.Index];
            Assert.True(before.Contains("Note(", StringComparison.Ordinal),
                "A numeric-box fallback is not reported to the user (no Note() before "
                + $"SettingsFallback.For at offset {f.Index}): the save would say OK about an entry "
                + "it has just discarded.");
        }
    }

    /// <summary>
    /// A VS Code palette command that tests the host's state <b>says so</b> when it is absent — it
    /// does not merely do nothing.
    /// </summary>
    /// <remarks>
    /// "Save session", "Load a session" and "Delete a session" left on a
    /// bare <c>return</c> when the host was not started: from the palette the command then has
    /// <b>no</b> visible effect — indistinguishable from a command that failed, or from a bug. The
    /// message-sending path, by contrast, says both states since 1.6.6
    /// (<c>hostUnavailableMessage</c> names the real cause: host stopped, or no folder open — in
    /// which case "restart the host" would be an inert remedy).
    /// </remarks>
    [Fact]
    public void VsCodeCommands_SayWhenTheHostIsMissing_InsteadOfDoingNothing()
    {
        var source = TsCode("chatViewProvider.ts");

        // Witness: the file really does carry commands and the host-state test.
        var commands = Regex.Matches(source, @"async (\w+Command)\s*\(");
        Assert.True(commands.Count >= 3,
            $"Only {commands.Count} command(s) found in chatViewProvider.ts — the rule guards "
            + "nothing any more.");
        Assert.Contains("hostUnavailableMessage", source, StringComparison.Ordinal);

        var offenders = new List<string>();
        foreach (Match c in commands)
        {
            // ⚠ The body is delimited by BRACES, not by a text window: the first version took
            // "up to the next command", so the file's last command swallowed everything after it —
            // and the rule accused `runSlashCommand`, which delegates to `send`, which has reported
            // the failure since 1.6.6. This is the lesson already written for the PowerShell scripts
            // ("a text window does not delimit a construct"), paid a second time in another
            // language.
            var body = MethodBody(source, c.Index);

            if (body.Contains("isRunning", StringComparison.Ordinal)
                && !body.Contains("hostUnavailableMessage", StringComparison.Ordinal))
                offenders.Add(c.Groups[1].Value);
        }

        Assert.True(offenders.Count == 0,
            "VS Code command(s) that probe the host and return without a word — from the palette "
            + "the user sees NOTHING: " + string.Join(", ", offenders));
    }

    // ── Plomberie ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The body of a TypeScript method, by brace matching from its signature.
    /// </summary>
    private static string MethodBody(string source, int signatureAt)
    {
        var open = source.IndexOf('{', signatureAt);
        Assert.True(open >= 0, "whileChatBusy has disappeared from hostClient.ts — the rule measures nothing.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }
        Assert.Fail("unterminated method body.");
        return string.Empty;
    }

    /// <summary>The repository root, found by walking up to the solution.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>A source under <c>vscode\src</c>, <b>comments neutralized</b>.</summary>
    private static string TsCode(string relative)
    {
        var path = Path.Combine(RepoRoot(), "vscode", "src",
                                relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"vscode/src/{relative} a disparu.");
        return NeutralizeTypeScriptComments(File.ReadAllText(path));
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
            "/* bloc :",
            "   banned(); */",
            "real(2);",
            "// real(3)",
            "function real(n: number) { return n; }",
        ]);

        var code = NeutralizeTypeScriptComments(source);

        // False RED: the prose documenting a forbidden pattern no longer carries it. The three
        // trailing comments are the traps: a regular expression taken for a string (because of the
        // quote in its character class), and a division taken for a regular expression, would
        // derail the rest of their line — hence let it through.
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
    /// The VS Code manifest and the code agree on commands: every declared command has a handler,
    /// and every keybinding or menu entry targets a command that exists.
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
    /// <para>Measured at zero divergence: 10 declared, 10 registered, 8 menu entries.</para>
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
            "The manifest and the code disagree about the commands — declared with no handler: "
            + $"[{string.Join(", ", declared.Except(registered).Order())}]; registered with no "
            + $"declaration (invisible in the palette): "
            + $"[{string.Join(", ", registered.Except(declared).Order())}].");

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
