using System.Globalization;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>
/// What a page offers to someone who does not see it or does not use a mouse, read once from the browser that captured it
/// (<c>view_page accessibility=true</c>): controls and images without a name, text whose contrast is under WCAG AA, what
/// the keyboard cannot reach, and the tab order — each with a CSS selector that finds it alone.
/// </summary>
/// <remarks>
/// ⚠ Names come from the browser's accessibility tree, never from a guess here: it computes the accessible name the way a
/// screen reader receives it (aria-labelledby, a wrapping label, alt="" as decoration), so what it says has a name has one.
/// ⚠ Contrast is computed on the colors as rendered, the background composed from the ancestors; a text over an image or a
/// gradient has no single background and is counted as not judged, never as passing.
/// </remarks>
internal sealed class PageAccessibility
{
    private const int MaxPerKind = 15;

    /// <summary>Whether the browser was read; <c>false</c> when the page was captured without its DevTools protocol.</summary>
    internal bool Read { get; private set; }

    /// <summary>Why the reading failed after the picture was taken; <c>null</c> when it did not.</summary>
    internal string? Failed { get; private set; }

    /// <summary>The width the page was read at.</summary>
    internal int Width { get; private set; }

    private readonly List<string> _unnamed = [], _images = [], _contrast = [], _keyboard = [], _tabOrder = [];
    private int _unnamedMore, _imagesMore, _contrastMore, _keyboardMore, _tabbable, _notJudged, _frames, _shadows;

    /// <summary>Says the reading did not finish: the capture's budget ran out after its picture.</summary>
    internal void Fail(string why) => Failed = why;

    /// <summary>Reads the page <paramref name="page"/> shows, at <paramref name="width"/>; a protocol failure is kept as
    /// <see cref="Failed"/>, never thrown: the picture it came with is still the answer.</summary>
    internal async Task ReadAsync(DevToolsConnection page, int width, CancellationToken ct)
    {
        Width = width;
        try
        {
            await ReadTreeAsync(page, ct).ConfigureAwait(false);
            var audit = await EvaluateAsync(page, AuditScript.Replace("__SELECTOR__", SelectorFunction, StringComparison.Ordinal)
                                                             .Replace("__MAX__", MaxPerKind.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), ct)
                                    .ConfigureAwait(false);
            Take(audit, "contrast", _contrast, ref _contrastMore);
            Take(audit, "keyboard", _keyboard, ref _keyboardMore);
            foreach (var entry in audit.GetProperty("tabOrder").EnumerateArray()) _tabOrder.Add(entry.GetString() ?? "");
            _tabbable = audit.GetProperty("tabbable").GetInt32();
            _notJudged = audit.GetProperty("notJudged").GetInt32();
            _frames = audit.GetProperty("frames").GetInt32();
            _shadows = audit.GetProperty("shadows").GetInt32();
            Read = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ChromiumDevTools.IsProtocolFailure(ex) || ex is DevToolsCallException)
        {
            Failed = ex.Message;
        }
    }

    private static void Take(JsonElement audit, string kind, List<string> into, ref int more)
    {
        foreach (var entry in audit.GetProperty(kind).EnumerateArray()) into.Add(entry.GetString() ?? "");
        more += audit.GetProperty(kind + "More").GetInt32();
    }

    /// <summary>The roles a person acts on: one without a name is a control a screen reader cannot say.</summary>
    private static readonly HashSet<string> Interactive = new(StringComparer.Ordinal)
    {
        "button", "link", "textbox", "searchbox", "combobox", "checkbox", "radio", "switch", "slider", "spinbutton",
        "tab", "menuitem", "menuitemcheckbox", "menuitemradio", "listbox", "treeitem",
    };

    /// <summary>The handles this reading makes in the page, released together at its end.</summary>
    private const string ObjectGroup = "inferpal-accessibility";

    /// <summary>At most this many images are asked what element they are (an svg?): the rest is counted as possibly more.</summary>
    private const int MaxImageChecks = 300;

    private async Task ReadTreeAsync(DevToolsConnection page, CancellationToken ct)
    {
        await page.CallAsync("DOM.enable", new { }, ct).ConfigureAwait(false);
        await page.CallAsync("Accessibility.enable", new { }, ct).ConfigureAwait(false);
        var tree = await page.CallAsync("Accessibility.getFullAXTree", new { }, ct).ConfigureAwait(false);
        var nodes = tree.GetProperty("nodes").EnumerateArray().ToList();
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (node.TryGetProperty("nodeId", out var nodeId) && nodeId.GetString() is { } key) byId[key] = node;
        var imageChecks = 0;
        try
        {
            foreach (var node in nodes)
            {
                if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean()) continue;
                var role = Value(node, "role");
                if (role is null || !node.TryGetProperty("backendDOMNodeId", out var id)) continue;
                var isImage = role is "image" or "img";
                if (!isImage && !Interactive.Contains(role)) continue;
                if (!string.IsNullOrWhiteSpace(Value(node, "name"))) continue;
                if (isImage)
                {
                    // ⚠ An icon inside a control is said by the control's name (one without a name is reported as that), and
                    // the browser makes every inline svg an image: an svg is one only when its role says so.
                    if (InsideAControl(node, byId)) continue;
                    if (++imageChecks > MaxImageChecks) { _imagesMore++; continue; }
                    if (!await IsAnImageAsync(page, id.GetInt32(), ct).ConfigureAwait(false)) continue;
                }
                var into = isImage ? _images : _unnamed;
                if (into.Count >= MaxPerKind) { if (isImage) _imagesMore++; else _unnamedMore++; continue; }
                if (await DescribeAsync(page, id.GetInt32(), ct).ConfigureAwait(false) is { } described)
                    into.Add((isImage ? "" : role + " ") + described);
            }
        }
        finally
        {
            try { await page.CallAsync("Runtime.releaseObjectGroup", new { objectGroup = ObjectGroup }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ChromiumDevTools.IsProtocolFailure(ex) || ex is DevToolsCallException) { /* the page is closing */ }
        }
    }

    /// <summary>Whether an ancestor of <paramref name="node"/> in the accessibility tree is a control.</summary>
    private static bool InsideAControl(JsonElement node, Dictionary<string, JsonElement> byId)
    {
        for (var depth = 0; depth < 200 && node.TryGetProperty("parentId", out var parent)
                            && parent.GetString() is { } key && byId.TryGetValue(key, out node); depth++)
            if (Value(node, "role") is { } role && Interactive.Contains(role)) return true;
        return false;
    }

    /// <summary>Whether the element behind an image node is an image a person is told about: not an svg without an image role.</summary>
    private static async Task<bool> IsAnImageAsync(DevToolsConnection page, int backendNodeId, CancellationToken ct)
    {
        JsonElement described;
        try { described = await page.CallAsync("DOM.describeNode", new { backendNodeId }, ct).ConfigureAwait(false); }
        catch (DevToolsCallException) { return false; }
        if (!described.TryGetProperty("node", out var node) || !node.TryGetProperty("nodeName", out var name)) return false;
        if (!string.Equals(name.GetString(), "svg", StringComparison.OrdinalIgnoreCase)) return true;
        if (!node.TryGetProperty("attributes", out var attributes)) return false;
        var pairs = attributes.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
        for (var i = 0; i + 1 < pairs.Count; i += 2)
            if (pairs[i] == "role" && pairs[i + 1].Trim().ToLowerInvariant() is "img" or "image") return true;
        return false;
    }

    private static string? Value(JsonElement node, string property) =>
        node.TryGetProperty(property, out var p) && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>The element's start tag, shortened, and the selector that finds it alone; <c>null</c> when it is gone.</summary>
    private static async Task<string?> DescribeAsync(DevToolsConnection page, int backendNodeId, CancellationToken ct)
    {
        JsonElement resolved;
        try { resolved = await page.CallAsync("DOM.resolveNode", new { backendNodeId, objectGroup = ObjectGroup }, ct).ConfigureAwait(false); }
        catch (DevToolsCallException) { return null; }   // a node of the tree with no element behind it
        if (!resolved.TryGetProperty("object", out var obj) || !obj.TryGetProperty("objectId", out var objectId)) return null;
        var called = await page.CallAsync("Runtime.callFunctionOn", new
        {
            objectId = objectId.GetString(),
            functionDeclaration = "function () { const sel = " + SelectorFunction + "; const e = this.nodeType === 1 ? this : this.parentElement; "
                                + "if (!e) return null; "
                                + "const tag = e.outerHTML.slice(0, e.outerHTML.indexOf('>') + 1); "
                                + "return (tag.length > 100 ? tag.slice(0, 99) + '…' : tag) + ' ' + sel(e); }",
            returnByValue = true,
        }, ct).ConfigureAwait(false);
        return called.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
               && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static async Task<JsonElement> EvaluateAsync(DevToolsConnection page, string expression, CancellationToken ct)
    {
        var evaluated = await page.CallAsync("Runtime.evaluate", new { expression, returnByValue = true }, ct).ConfigureAwait(false);
        if (evaluated.TryGetProperty("exceptionDetails", out var thrown))
            throw new InvalidOperationException("the accessibility script failed: " + thrown.GetRawText()[..Math.Min(200, thrown.GetRawText().Length)]);
        return evaluated.GetProperty("result").GetProperty("value").Clone();
    }

    /// <summary>What the model reads, under the capture's lines.</summary>
    internal IEnumerable<string> Lines()
    {
        if (Failed is not null)
        {
            yield return $"Accessibility: not read — the browser's protocol failed while reading it ({Failed}).";
            yield break;
        }
        var found = _unnamed.Count + _images.Count + _contrast.Count + _keyboard.Count;
        yield return $"Accessibility, read at {Width.ToString(CultureInfo.InvariantCulture)} px from the browser's accessibility tree " +
                     "and the colors it rendered:";
        foreach (var line in Section($"control{S(_unnamed.Count + _unnamedMore)} with no name a screen reader can say " +
                                     "(give it text, aria-label or a <label>)", _unnamed, _unnamedMore)) yield return line;
        foreach (var line in Section($"image{S(_images.Count + _imagesMore)} with no text alternative (alt; alt=\"\" if it " +
                                     "is decoration)", _images, _imagesMore)) yield return line;
        foreach (var line in Section($"text{S(_contrast.Count + _contrastMore)} under WCAG AA contrast (4.5:1, 3:1 for large " +
                                     "text), lowest first", _contrast, _contrastMore)) yield return line;
        foreach (var line in Section($"keyboard problem{S(_keyboard.Count + _keyboardMore)}", _keyboard, _keyboardMore)) yield return line;
        if (found == 0)
            yield return "- No control or image without a name, no text under AA contrast " +
                         (_notJudged > 0 ? "among the texts measured " : "") + "and nothing out of the keyboard's reach was found.";
        if (_notJudged > 0)
            yield return $"- Not judged for contrast: {_notJudged.ToString(CultureInfo.InvariantCulture)} text{S(_notJudged)} with no " +
                         "single background to measure against (over an image, a gradient or another element), or in a color " +
                         "that is transparent or could not be read — look at the picture for those.";
        if (_frames > 0 || _shadows > 0)
            yield return "- Not read: " + string.Join(" and ", new[]
            {
                _frames > 0 ? $"{_frames.ToString(CultureInfo.InvariantCulture)} frame{S(_frames)} (iframe)" : null,
                _shadows > 0 ? $"{_shadows.ToString(CultureInfo.InvariantCulture)} shadow root{S(_shadows)} (web components)" : null,
            }.Where(p => p is not null)) + " — what they hold is in none of the lists above.";
        yield return _tabbable == 0
            ? "- Tab order: nothing on the page takes the keyboard's focus."
            : $"- Tab order ({(_tabbable > _tabOrder.Count ? $"first {_tabOrder.Count.ToString(CultureInfo.InvariantCulture)} of " : "")}" +
              $"{_tabbable.ToString(CultureInfo.InvariantCulture)}): " + string.Join("; ", _tabOrder);
        yield return "- Not checked: whether the focus is visible, click handlers attached by script, text drawn inside images or " +
                     "by ::before and ::after, placeholder text, a text faded by its ancestors' opacity, " +
                     "and what only shows after an action (open a menu with interact_page, then look again).";
    }

    private static IEnumerable<string> Section(string title, List<string> items, int more)
    {
        if (items.Count == 0) yield break;
        yield return $"- {(items.Count + more).ToString(CultureInfo.InvariantCulture)} {title}:";
        foreach (var item in items) yield return "  " + item;
        if (more > 0) yield return $"  … and {more.ToString(CultureInfo.InvariantCulture)} more not listed.";
    }

    private static string S(int count) => count == 1 ? "" : "s";

    /// <summary>The selector that finds an element alone — the one <c>interact_page</c>'s controls are given by too
    /// (<c>PageSession</c>'s controls script takes it from here).</summary>
    /// <remarks>An attribute longer than a selector a person reads (a <c>data:</c> image, a long link) is never used.</remarks>
    internal const string SelectorFunction = """
        ((e) => {
          const one = (s) => { try { return document.querySelectorAll(s).length === 1; } catch { return false; } };
          if (e.id && one("#" + CSS.escape(e.id))) return "#" + CSS.escape(e.id);
          const tag = e.tagName.toLowerCase();
          for (const a of ["name", "data-testid", "aria-label", "placeholder", "src", "href"]) {
            const v = e.getAttribute(a);
            if (v && v.length <= 80) { const s = tag + "[" + a + "=" + JSON.stringify(v) + "]"; if (one(s)) return s; }
          }
          const parts = [];
          for (let n = e; n && n !== document.body && n !== document.documentElement; n = n.parentElement) {
            if (n.id && one("#" + CSS.escape(n.id))) { parts.unshift("#" + CSS.escape(n.id)); return parts.join(" > "); }
            let i = 1; for (let s = n.previousElementSibling; s; s = s.previousElementSibling) if (s.tagName === n.tagName) i++;
            parts.unshift(n.tagName.toLowerCase() + ":nth-of-type(" + i + ")");
          }
          return ["body", ...parts].join(" > ");
        })
        """;

    /// <summary>Contrast, keyboard reach and tab order, computed in the page. ⚠ It focuses elements to see whether they
    /// show when focused, and gives the focus back where it was.</summary>
    private const string AuditScript = """
        (() => {
          const sel = __SELECTOR__;
          const MAX = __MAX__;
          // Any CSS color as rendered (oklch, lab, color-mix included), through a canvas; null when it cannot be read.
          const ctx = document.createElement("canvas").getContext("2d", { willReadFrequently: true });
          const parse = (c) => {
            if (!c || !ctx) return null;
            const m = c.match(/^rgba?\(([^)]+)\)$/);
            if (m) {
              const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(Number);
              if (p.length >= 3 && p.every((v) => !Number.isNaN(v))) return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
            }
            ctx.clearRect(0, 0, 1, 1);
            ctx.fillStyle = "#010203"; const sentinel = ctx.fillStyle;
            ctx.fillStyle = c;
            if (ctx.fillStyle === sentinel && c !== "#010203") return null;   // not a color the canvas reads
            ctx.fillRect(0, 0, 1, 1);
            const d = ctx.getImageData(0, 0, 1, 1).data;
            return { r: d[0], g: d[1], b: d[2], a: d[3] / 255 };
          };
          const over = (top, bottom) => ({
            r: top.r * top.a + bottom.r * (1 - top.a), g: top.g * top.a + bottom.g * (1 - top.a),
            b: top.b * top.a + bottom.b * (1 - top.a), a: 1 });
          const lum = (c) => {
            const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
          };
          const hex = (c) => "#" + [c.r, c.g, c.b].map((v) => Math.round(v).toString(16).padStart(2, "0")).join("");
          // Seen: laid out, and neither it nor an ancestor hidden or fully transparent.
          const shown = (e) => {
            const r = e.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) return false;
            if (e.checkVisibility) return e.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true, opacityProperty: true, visibilityProperty: true });
            const st = getComputedStyle(e);
            return e.getClientRects().length > 0 && st.visibility !== "hidden" && Number(st.opacity) > 0;
          };
          // Laid out at all (an ancestor display:none, a closed <details> or <dialog>, [hidden] leave it out).
          const rendered = (e) => e.checkVisibility ? e.checkVisibility({ checkVisibilityCSS: true, visibilityProperty: true })
                                                    : e.getClientRects().length > 0 && getComputedStyle(e).visibility !== "hidden";
          // The page's own base: what the browser paints under everything (dark under a dark color-scheme).
          const probe = document.createElement("div");
          probe.style.cssText = "position:absolute;width:0;height:0;background-color:Canvas";
          document.documentElement.appendChild(probe);
          const canvasBase = parse(getComputedStyle(probe).backgroundColor) || { r: 255, g: 255, b: 255, a: 1 };
          probe.remove();
          // The background behind an element: its own color and its ancestors', composed; null over an image or a
          // gradient, a color that cannot be read, or anything painted between the text and that background.
          const background = (e) => {
            const layers = []; let owner = null;
            for (let n = e; n; n = n.parentElement) {
              const st = getComputedStyle(n);
              if (st.backgroundImage && st.backgroundImage !== "none") return null;
              const c = parse(st.backgroundColor);
              if (!c) return null;
              if (c.a > 0) { layers.push(c); if (c.a >= 1) { owner = n; break; } }
            }
            const r = e.getBoundingClientRect();
            const x = r.left + Math.min(r.width / 2, 8), y = r.top + r.height / 2;
            if (x >= 0 && y >= 0 && x < innerWidth && y < innerHeight) {
              for (const under of document.elementsFromPoint(x, y)) {
                if (under === owner || under === document.documentElement || under === document.body && !owner) break;
                if (under === e || under.contains(e)) continue;
                if (e.contains(under)) continue;
                const tag = under.tagName.toLowerCase();
                const ust = getComputedStyle(under);
                const painted = ["img", "video", "canvas", "svg", "iframe", "picture", "object", "embed"].includes(tag)
                  || (ust.backgroundImage && ust.backgroundImage !== "none")
                  || (parse(ust.backgroundColor) || { a: 1 }).a > 0;
                if (painted) return null;   // the text sits over another element: no single background to measure
              }
            }
            let base = { ...canvasBase, a: 1 };
            for (let i = layers.length - 1; i >= 0; i--) base = over(layers[i], base);
            return base;
          };

          const contrast = [], judged = new Set(); let contrastMore = 0, notJudged = 0;
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          for (let t = walker.nextNode(); t; t = walker.nextNode()) {
            const e = t.parentElement;
            if (!e || judged.has(e) || !t.textContent.trim()) continue;
            if (["SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "OPTION"].includes(e.tagName)) continue;
            judged.add(e);
            if (!shown(e) || e.closest(":disabled, [aria-disabled=true]")) continue;
            const st = getComputedStyle(e);
            const fill = st.webkitTextFillColor;
            const fg = parse(fill && fill !== st.color ? fill : st.color);
            if (!fg || fg.a === 0 || (st.backgroundClip || "").includes("text")) { notJudged++; continue; }
            const bg = background(e);
            if (!bg) { notJudged++; continue; }
            const color = over(fg, bg);
            const l1 = lum(color), l2 = lum(bg);
            const ratio = (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
            const size = parseFloat(st.fontSize), bold = Number(st.fontWeight) >= 700;
            const large = size >= 24 || (bold && size >= 18.66);
            const needed = large ? 3 : 4.5;
            if (ratio >= needed) continue;
            const text = t.textContent.trim().replace(/\s+/g, " ");
            contrast.push({ ratio, line: ratio.toFixed(2) + ":1 (needs " + needed + (large ? ", large text" : "") + ") "
              + hex(color) + " on " + hex(bg) + " " + JSON.stringify(text.length > 40 ? text.slice(0, 39) + "…" : text) + " " + sel(e) });
          }
          contrast.sort((a, b) => a.ratio - b.ratio);
          if (contrast.length > MAX) { contrastMore = contrast.length - MAX; contrast.length = MAX; }

          const keyboard = []; let keyboardMore = 0;
          const push = (line) => { if (keyboard.length < MAX) keyboard.push(line); else keyboardMore++; };
          const focusable = "a[href], button, input, select, textarea, summary, iframe, [tabindex], [contenteditable=true]";
          const all = [...document.querySelectorAll(focusable)].filter((e) => !e.disabled && e.tabIndex >= 0
            && !(e.tagName === "INPUT" && e.type === "hidden") && !e.closest("[inert]") && rendered(e));
          for (const e of all) if (e.tabIndex > 0) push("tabindex=" + e.tabIndex + " puts it ahead of the page's order: " + sel(e));
          // What a mouse clicks and the keyboard cannot reach: a click handler written in the markup on an element no Tab lands on.
          for (const e of document.querySelectorAll("[onclick]")) {
            if (!shown(e) || e.tabIndex >= 0 || e.closest(focusable) || e.querySelector(focusable)) continue;
            push("clickable (onclick) but out of the keyboard's reach (no tabindex, not a button or link): " + sel(e));
          }
          // What takes the focus and still cannot be seen once focused (a skip link that shows on focus is fine; so is a
          // styled checkbox, radio or file field whose visible label shows the focus).
          const labelShown = (e) => (e.labels && [...e.labels].some(shown)) || (e.parentElement && e.parentElement.tagName === "LABEL" && shown(e.parentElement));
          const before = document.activeElement;
          for (const e of all) {
            if (shown(e) || labelShown(e)) continue;
            try { e.focus({ preventScroll: true }); } catch { continue; }
            if (document.activeElement === e && !shown(e)) push("takes the focus but stays unseen when focused: " + sel(e));
          }
          try { if (before && before.focus) before.focus({ preventScroll: true }); else document.activeElement.blur(); } catch { }

          const ordered = [...all.filter((e) => e.tabIndex > 0).sort((a, b) => a.tabIndex - b.tabIndex), ...all.filter((e) => e.tabIndex === 0)];
          const labelledBy = (e) => (e.getAttribute("aria-labelledby") || "").split(/\s+/)
            .map((id) => document.getElementById(id)).filter(Boolean).map((l) => l.innerText).join(" ");
          const name = (e) => (e.getAttribute("aria-label") || labelledBy(e) || (e.labels && e.labels[0] ? e.labels[0].innerText : "")
            || e.innerText || e.getAttribute("alt") || e.getAttribute("title") || e.getAttribute("placeholder") || e.value || "")
            .toString().trim().replace(/\s+/g, " ").slice(0, 40);
          const tabOrder = ordered.slice(0, 15).map((e) => JSON.stringify(name(e)) + " " + sel(e));
          // What this reading does not enter, named when the page has any.
          const frames = document.querySelectorAll("iframe, frame").length;
          let shadows = 0; for (const e of document.querySelectorAll("*")) if (e.shadowRoot) shadows++;
          return { contrast: contrast.map((c) => c.line), contrastMore, notJudged, keyboard, keyboardMore,
                   tabOrder, tabbable: ordered.length, frames, shadows };
        })()
        """;
}
