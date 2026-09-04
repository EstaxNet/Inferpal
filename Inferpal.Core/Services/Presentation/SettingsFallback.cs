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
}
