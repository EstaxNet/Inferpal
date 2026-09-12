namespace Inferpal.Services.Presentation;

/// <summary>
/// What a numeric settings box must be worth when it could not be read.
/// </summary>
/// <remarks>
/// <para>
/// An empty box and an unreadable box do not mean the same thing, and confusing the two costs the
/// user a value. <b>Empty</b> = they cleared the box, which is the existing affordance for "give
/// me the default back". <b>Non-empty but unreadable</b> = they typed something that does not
/// parse — <c>4o</c> for <c>40</c>, a comma where the culture expects a dot — and a typo must not
/// <b>destroy</b> a setting.
/// </para>
/// <para>
/// ⚠ Measured in the Visual Studio settings window: the nine unsanitized numeric boxes read
/// <c>int.TryParse(text, out var v) ? clamp(v) : &lt;factory default&gt;</c>. A single typo
/// therefore reset that setting to its factory value — <c>AgentMaxIterations</c> from 40 to 20,
/// <c>RagTopK</c> to 5 — the save reported "settings saved", and the box kept showing what the
/// user had typed. The VS Code panel had the same class one notch milder: it keeps the previous
/// value and merely says nothing.
/// </para>
/// <para>
/// The silence was settled separately: <b>we save, and we name the ignored fields</b> — refusing
/// the whole save would also throw away the other valid edits of the same form.
/// <see cref="WasIgnored"/> decides what gets named, and both panels render the same sentence
/// (<c>Strings.SettingsFieldsIgnored</c>).
/// </para>
/// </remarks>
internal static class SettingsFallback
{
    /// <summary>
    /// The value to keep when reading <paramref name="text"/> failed:
    /// <paramref name="whenCleared"/> if the box is empty, otherwise <paramref name="current"/>.
    /// </summary>
    /// <param name="text">The raw text of the box, exactly as the user left it.</param>
    /// <param name="current">What the configuration carries today — never lost to a typo.</param>
    /// <param name="whenCleared">The default, returned only when the box was cleared on purpose.</param>
    public static T For<T>(string? text, T current, T whenCleared) =>
        string.IsNullOrWhiteSpace(text) ? whenCleared : current;

    /// <summary>
    /// Must the field be <b>named</b> to the user as ignored?
    /// </summary>
    /// <param name="text">The raw text of the box.</param>
    /// <param name="applied">
    /// Was the input kept? This is the call site's <em>whole</em> guard, not just its
    /// <c>TryParse</c>: a value that parses but the site rejects (a negative VRAM budget, a zero
    /// where the minimum is 1) was applied no more than a typo was, and saying so is the same
    /// service.
    /// </param>
    /// <remarks>
    /// ⚠ An <b>empty</b> box is never named: clearing it is the existing affordance for "give me
    /// the default back" (see <see cref="For{T}"/>), so its effect is intended, not suffered.
    /// Naming only what was typed and did not take is what keeps the list short and readable.
    /// </remarks>
    public static bool WasIgnored(string? text, bool applied) =>
        !applied && !string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// A field label as it is <b>quoted inside a sentence</b>: without its trailing colon.
    /// </summary>
    /// <remarks>
    /// Form labels are written to sit in front of a box — five of the nine numeric boxes end with
    /// ":", the other four do not. Quoted as-is inside an enumeration that reads "Context window
    /// :, Results per query :". The fullwidth colon is there for Japanese and Chinese, the
    /// non-breaking space for French.
    /// ⚠ The VS Code panel does the same to the same labels (it receives them from the host, out
    /// of these very .resx files): <c>webview\settings.ts</c>, <c>labelForSentence</c>.
    /// </remarks>
    public static string LabelForSentence(string label) =>
        label.TrimEnd(' ', '\u00A0', '\u202F', ':', '\uFF1A');   // space, NBSP, narrow NBSP, colon, fullwidth colon

    /// <summary>
    /// The code behind a <b>dropdown</b>: by index first (the only landmark a translation does not
    /// move), by label next, and <paramref name="current"/> when neither answers.
    /// <paramref name="matched"/> tells the two cases apart, for <see cref="WasIgnored"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Same rule as <see cref="For{T}"/>, and it cost more here than on the numeric boxes: the
    /// three dropdowns of the Visual Studio form resolved <b>by label</b> with a fallback to the
    /// <b>factory default</b>. An unrecognised label therefore reset the language to "follow Visual
    /// Studio", the inline mode to "Default" -- and above all the <b>backend</b> to Ollama: the user
    /// had LM Studio on screen and the product was talking to something else, without a word
    /// (issue #8 describes exactly that state). The inline mode had been fixed on its own in 1.6.8;
    /// the other two stayed three lines above and below it.
    ///
    /// ⚠ The index comes <b>before</b> the label because translated labels move under the
    /// comparison when the language changes in the same save. Pass <c>-1</c> when the front-end
    /// exposes no index.
    /// </remarks>
    public static string ResolveSelection(
        IReadOnlyList<(string Code, string Name)> options, int index, string? name,
        string current, out bool matched)
    {
        if (index >= 0 && index < options.Count)
        {
            matched = true;
            return options[index].Code;
        }

        foreach (var option in options)
            if (option.Name == name)
            {
                matched = true;
                return option.Code;
            }

        matched = false;
        return current;
    }

    /// <summary>
    /// The value of a dropdown whose <c>SelectedItem</c> is bound, at save time: the selection,
    /// otherwise <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>null</c> is never a user choice. A <c>Selector</c> writes it into the bound property when
    /// the selected item leaves its collection, and under Remote UI that write comes back after the
    /// update that caused it. Unassigning a role goes through the list's empty entry ("same as the
    /// chat model"), which arrives here as <c>""</c>. Writing <c>""</c> over a <c>null</c> would
    /// therefore erase a configured model because a backend did not list it when the window opened.
    /// <paramref name="lost"/> is true only when there was something to keep: a role never
    /// configured that stays empty has nothing to report.
    /// </remarks>
    public static string KeepSelection(string? selected, string current, out bool lost)
    {
        if (selected is not null)
        {
            lost = false;
            return selected.Trim();
        }

        lost = !string.IsNullOrEmpty(current);
        return current;
    }
}
