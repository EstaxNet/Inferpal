using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Localization;

namespace Inferpal.Services.Mcp;

/// <summary>What a field of a server's form takes.</summary>
internal enum McpInputKind { Text, Number, Integer, Boolean, Choice, Choices }

/// <summary>A text field's format, checked before the answer is sent.</summary>
internal enum McpTextFormat { None, Email, Uri, Date, DateTime }

/// <summary>One choice of a <see cref="McpInputKind.Choice"/> or <see cref="McpInputKind.Choices"/> field: the value
/// sent, and the label shown.</summary>
internal sealed record McpInputOption(string Value, string Label);

/// <summary>A field's value as the card holds it: typed text (a number, a date, the value of the one choice), a box
/// ticked or not, or the values of the choices ticked.</summary>
internal sealed record McpInputValue(string? Text = null, bool? Flag = null, IReadOnlyList<string>? Picks = null);

/// <summary>One field of a server's form, as its schema declares it.</summary>
internal sealed record McpInputField(
    string Key, McpInputKind Kind, string Label, string? Description, bool Required,
    McpTextFormat Format = McpTextFormat.None, int? MinLength = null, int? MaxLength = null,
    double? Minimum = null, double? Maximum = null,
    IReadOnlyList<McpInputOption>? Options = null, int? MinItems = null, int? MaxItems = null,
    McpInputValue? Default = null)
{
    /// <summary>The choices of a choice field; none for the other kinds.</summary>
    public IReadOnlyList<McpInputOption> Choices => Options ?? [];
}

/// <summary>What the person did with a server's form.</summary>
/// <remarks><see cref="SetAside"/>: a note typed while the card waited took it away. <see cref="NotShown"/>: the editor
/// could not draw it. Both reach the server as <c>cancel</c>, the answer for "dismissed without an explicit choice".</remarks>
internal enum McpInputAction { Accept, Decline, Cancel, SetAside, NotShown }

/// <summary>The person's answer to a server's form: what they did, and the values sent when they sent them.</summary>
internal sealed record McpInputAnswer(McpInputAction Action, JsonObject? Content = null)
{
    /// <summary>The <c>ElicitResult</c> the retried request carries under its key.</summary>
    public JsonObject Result()
    {
        var result = new JsonObject
        {
            ["action"] = Action switch
            {
                McpInputAction.Accept  => "accept",
                McpInputAction.Decline => "decline",
                _                      => "cancel",
            },
        };
        if (Action == McpInputAction.Accept) result["content"] = Content?.DeepClone() ?? new JsonObject();
        return result;
    }

    /// <summary>
    /// The line the model reads above the tool's result: what the person did with the server's question.
    /// </summary>
    /// <remarks>⚠ Without it, a server that answers a refusal with an error leaves the model to guess, and the next
    /// call asks the person the same question again. The values themselves go to the server only.</remarks>
    public string NoteForModel(string server) => Action switch
    {
        McpInputAction.Accept   => $"[The user answered the question MCP server '{server}' asked; the answer went to the server, not to you.]",
        McpInputAction.Decline  => $"[The user declined to answer the question MCP server '{server}' asked. Do not ask the same thing again unless the user says so.]",
        McpInputAction.SetAside => $"[The user wrote you a note instead of answering the question MCP server '{server}' asked; the server was told it was cancelled.]",
        McpInputAction.NotShown => $"[MCP server '{server}' asked the user a question that could not be shown here; the server was told it was cancelled.]",
        _                       => $"[The user closed the question MCP server '{server}' asked without answering it.]",
    };
}

/// <summary>Asks the person to fill a server's form, and answers what they did (<see cref="Execution.IApprovalService.AskForInputAsync"/>).</summary>
internal delegate Task<McpInputAnswer> McpInputAsker(McpInputForm form, CancellationToken ct);

/// <summary>The outcome of checking a card's values: the content to send, or what to fix, by field.</summary>
internal sealed record McpInputCheck(JsonObject? Content, IReadOnlyDictionary<string, string> Errors)
{
    public bool Passed => Content is not null;
}

/// <summary>
/// A form an MCP server asks the person to fill (<c>elicitation/create</c>, form mode, revision 2026-07-28): the server
/// that asks, the call it asks for, its message, and its fields.
/// </summary>
/// <remarks>
/// The schema is the specification's restricted one: an object whose properties are text (formats email, uri, date,
/// date-time, length bounds), numbers (bounds), booleans, or one or several choices. Anything else is refused by
/// <see cref="Read"/>, never guessed: a field drawn as something it is not sends the server a value it did not ask for.
/// </remarks>
internal sealed record McpInputForm(string Server, string Tool, string Message, IReadOnlyList<McpInputField> Fields)
{
    /// <summary>
    /// Reads the parameters of an <c>elicitation/create</c> request; <c>null</c> with <paramref name="refusal"/> (said to
    /// the model, in English) when it is not a form this client can show.
    /// </summary>
    public static McpInputForm? Read(string server, string tool, JsonElement @params, out string? refusal)
    {
        refusal = null;
        if (@params.ValueKind != JsonValueKind.Object)
        {
            refusal = "its request carries no parameters";
            return null;
        }
        if (Str(@params, "mode") is { } mode && mode != "form")
        {
            refusal = mode == "url"
                ? "it asked for a web page to be opened (url mode), which Inferpal does not offer"
                : $"it asked in a mode Inferpal does not know ('{mode}')";
            return null;
        }
        if (Str(@params, "message") is not { } message || string.IsNullOrWhiteSpace(message))
        {
            refusal = "its question has no message, so the user could not be told why it asks";
            return null;
        }
        if (!@params.TryGetProperty("requestedSchema", out var schema) || schema.ValueKind != JsonValueKind.Object
            || Str(schema, "type") is { } type && type != "object")
        {
            refusal = "its form is not an object schema";
            return null;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String && r.GetString() is { } name) required.Add(name);

        var fields = new List<McpInputField>();
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                if (ReadField(p.Name, p.Value, required.Contains(p.Name)) is { } field) fields.Add(field);
                else
                {
                    refusal = $"its field '{p.Name}' is of a kind a form cannot show ({Describe(p.Value)})";
                    return null;
                }
            }
        }
        return new McpInputForm(server, tool, message.Trim(), fields);
    }

    /// <summary>The values the card starts with: each field's default, when the schema gives one of its kind.</summary>
    public IReadOnlyDictionary<string, McpInputValue> Initial() =>
        Fields.Where(f => f.Default is not null).ToDictionary(f => f.Key, f => f.Default!, StringComparer.Ordinal);

    /// <summary>
    /// Checks the card's values against the schema: the content to send when every field passes, else one sentence per
    /// field to fix, in the person's language.
    /// </summary>
    /// <remarks>
    /// ⚠ Checked here, for both editors: a value that breaks the schema reaches the server as an answer the person
    /// thought was accepted, and the server's refusal reaches the model, not the person who could fix it.
    /// </remarks>
    public McpInputCheck Check(IReadOnlyDictionary<string, McpInputValue> values)
    {
        var content = new JsonObject();
        var errors  = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Fields)
        {
            values.TryGetValue(f.Key, out var v);
            var error = CheckField(f, v, out var node);
            if (error is not null) errors[f.Key] = error;
            else if (node is not null) content[f.Key] = node;
        }
        return errors.Count == 0 ? new McpInputCheck(content, errors) : new McpInputCheck(null, errors);
    }

    /// <summary>The hint under a field: its description, then what it takes; empty when there is nothing to say.</summary>
    /// <param name="f">The field.</param>
    /// <param name="takes">Say what the field takes. Not under a field that has something to fix: its error says it.</param>
    public static string Hint(McpInputField f, bool takes = true)
    {
        var what = !takes ? null : f.Kind switch
        {
            McpInputKind.Text    => TextHint(f),
            McpInputKind.Number  => RangeText(f, whole: false),
            McpInputKind.Integer => RangeText(f, whole: true),
            McpInputKind.Choices => ChoiceCount(f),
            _                    => null,
        };
        return string.Join(" · ", new[] { f.Description?.Trim(), what }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>A field whose name or label reads like a secret: the card warns under it that a server must not ask for
    /// one (the specification forbids it in a form).</summary>
    /// <remarks>Words of the key and the label, split on case and punctuation — never a substring: "pinned", "tokenizer
    /// name" and "passport" are not secrets.</remarks>
    public static bool LooksSecret(McpInputField f)
    {
        var words = Words(f.Key).Concat(Words(f.Label)).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (SecretWords.Contains(w)) return true;
            if (i + 1 < words.Count && SecretPairs.Contains(w + " " + words[i + 1])) return true;
        }
        return false;
    }

    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "password", "passwd", "passphrase", "passcode", "pwd", "pin", "secret", "token", "apikey", "credential", "credentials",
        "otp", "cvv", "cvc", "pat", "iban", "ssn",
    };

    private static readonly HashSet<string> SecretPairs = new(StringComparer.Ordinal)
    {
        "api key", "access key", "private key", "secret key", "card number", "security code",
    };

    private static IEnumerable<string> Words(string text)
    {
        var word = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (var ch in text)
        {
            var split = !char.IsLetterOrDigit(ch) || char.IsUpper(ch) && char.IsLower(prev);
            if (split && word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
            if (char.IsLetterOrDigit(ch)) word.Append(char.ToLowerInvariant(ch));
            prev = ch;
        }
        if (word.Length > 0) yield return word.ToString();
    }

    // ── Reading the schema ──────────────────────────────────────────────────────────────────────────────────────────

    private static McpInputField? ReadField(string key, JsonElement s, bool required)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        var label = Str(s, "title") is { Length: > 0 } title ? title.Trim() : key;
        var description = Str(s, "description");

        switch (Str(s, "type"))
        {
            case "string":
                if ((TitledOptions(s, "oneOf") ?? EnumOptions(s)) is { } choices)
                {
                    var pick = Str(s, "default") is { } d && choices.Any(o => o.Value == d) ? new McpInputValue(Text: d) : null;
                    return new McpInputField(key, McpInputKind.Choice, label, description, required, Options: choices, Default: pick);
                }
                McpTextFormat format;
                switch (Str(s, "format"))
                {
                    case null:        format = McpTextFormat.None;     break;
                    case "email":     format = McpTextFormat.Email;    break;
                    case "uri":       format = McpTextFormat.Uri;      break;
                    case "date":      format = McpTextFormat.Date;     break;
                    case "date-time": format = McpTextFormat.DateTime; break;
                    default:          return null;
                }
                return new McpInputField(key, McpInputKind.Text, label, description, required, format,
                                         MinLength: Int(s, "minLength"), MaxLength: Int(s, "maxLength"),
                                         Default: Str(s, "default") is { } text ? new McpInputValue(Text: text) : null);

            case "number":
            case "integer":
                var whole = Str(s, "type") == "integer";
                var def = Num(s, "default") is { } n && (!whole || n == Math.Floor(n)) ? new McpInputValue(Text: n.ToString("R", CultureInfo.InvariantCulture)) : null;
                return new McpInputField(key, whole ? McpInputKind.Integer : McpInputKind.Number, label, description, required,
                                         Minimum: Num(s, "minimum"), Maximum: Num(s, "maximum"), Default: def);

            case "boolean":
                return new McpInputField(key, McpInputKind.Boolean, label, description, required,
                                         Default: s.TryGetProperty("default", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False
                                             ? new McpInputValue(Flag: b.GetBoolean())
                                             : null);

            case "array":
                if (!s.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object) return null;
                if ((TitledOptions(items, "anyOf") ?? EnumOptions(items)) is not { } many) return null;
                IReadOnlyList<string>? picks = null;
                if (s.TryGetProperty("default", out var dd) && dd.ValueKind == JsonValueKind.Array)
                    picks = [.. dd.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                                 .Where(v => many.Any(o => o.Value == v))];
                return new McpInputField(key, McpInputKind.Choices, label, description, required, Options: many,
                                         MinItems: Int(s, "minItems"), MaxItems: Int(s, "maxItems"),
                                         Default: picks is null ? null : new McpInputValue(Picks: picks));
        }
        return null;
    }

    /// <summary>Choices with their own labels (<c>oneOf</c> / <c>anyOf</c> of <c>const</c> + <c>title</c>).</summary>
    private static List<McpInputOption>? TitledOptions(JsonElement s, string member)
    {
        if (!s.TryGetProperty(member, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var options = new List<McpInputOption>();
        foreach (var o in list.EnumerateArray())
        {
            if (Str(o, "const") is not { } value) return null;
            options.Add(new McpInputOption(value, Str(o, "title") is { Length: > 0 } t ? t : value));
        }
        return options.Count > 0 ? options : null;
    }

    /// <summary>Choices named by their values (<c>enum</c>), labelled by the legacy <c>enumNames</c> when it has one per
    /// value.</summary>
    private static List<McpInputOption>? EnumOptions(JsonElement s)
    {
        if (!s.TryGetProperty("enum", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var values = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        if (values.Count == 0 || values.Count != list.GetArrayLength()) return null;
        var names = s.TryGetProperty("enumNames", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList()
            : null;
        return [.. values.Select((v, i) => new McpInputOption(v,
            names is not null && names.Count == values.Count && names[i] is { Length: > 0 } label ? label : v))];
    }

    private static string Describe(JsonElement s) =>
        s.ValueKind != JsonValueKind.Object ? "not a schema"
        : Str(s, "type") is { } t ? (Str(s, "format") is { } f ? $"type '{t}', format '{f}'" : $"type '{t}'")
        : "no type";

    // ── Checking the values ─────────────────────────────────────────────────────────────────────────────────────────

    private static string? CheckField(McpInputField f, McpInputValue? v, out JsonNode? node)
    {
        node = null;
        switch (f.Kind)
        {
            case McpInputKind.Boolean:
                // A box always says something: unticked is "no", and is sent as such.
                node = JsonValue.Create(v?.Flag ?? false);
                return null;

            case McpInputKind.Choices:
                var picks = (v?.Picks ?? []).Distinct(StringComparer.Ordinal).Where(p => f.Choices.Any(o => o.Value == p)).ToList();
                if (picks.Count == 0)
                {
                    // Not required, nothing ticked is not sent at all: the bounds are for a set that is sent.
                    if (!f.Required) return null;
                    // Marked required, a field with nothing ticked is one the person passed over, as an empty box is —
                    // never an empty choice they made. A server that takes none says so with minItems 0.
                    if (f.MinItems is null) return Strings.InputChooseAtLeast(1);
                }
                if (f.MinItems is { } least && picks.Count < least || f.MaxItems is { } most && picks.Count > most)
                    return ChoiceCount(f);
                // In the schema's order, not the order they were ticked in.
                node = new JsonArray([.. f.Choices.Where(o => picks.Contains(o.Value)).Select(o => (JsonNode)JsonValue.Create(o.Value))]);
                return null;
        }

        var text = v?.Text;
        if (string.IsNullOrWhiteSpace(text)) return f.Required ? Missing(f) : null;

        switch (f.Kind)
        {
            case McpInputKind.Choice:
                if (!f.Choices.Any(o => o.Value == text)) return Strings.InputChooseOne;
                node = JsonValue.Create(text);
                return null;

            case McpInputKind.Number:
            case McpInputKind.Integer:
                var whole = f.Kind == McpInputKind.Integer;
                // A whole number is read as one first: through a double, 9223372036854775807 is 2^63, one past the range.
                long? exact = whole && long.TryParse(Typed(text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l : null;
                if ((exact ?? ParseNumber(text, decimalComma: !whole && DecimalComma)) is not { } n || whole && n != Math.Floor(n)
                    || f.Minimum is { } min && n < min || f.Maximum is { } max && n > max)
                    return RangeText(f, whole) ?? (whole ? Strings.InputWhole : Strings.InputNumber);
                // ⚠ long.MaxValue as a double is 2^63, one past it: the bound is strict, or 2^63 casts to long.MinValue.
                node = exact is { } kept ? JsonValue.Create(kept)
                     : whole && n >= -9223372036854775808d && n < 9223372036854775808d ? JsonValue.Create((long)n)
                     : JsonValue.Create(n);
                return null;

            default:
                var length = text.EnumerateRunes().Count();
                if (f.MinLength is { } shortest && length < shortest || f.MaxLength is { } longest && length > longest)
                    return LengthText(f);
                if (!Fits(f.Format, text.Trim())) return FormatError(f.Format);
                // A format is checked trimmed, and sent trimmed; free text goes as typed.
                node = JsonValue.Create(f.Format == McpTextFormat.None ? text : text.Trim());
                return null;
        }
    }

    private static string Missing(McpInputField f) => f.Kind == McpInputKind.Choice ? Strings.InputChooseOne : Strings.InputErrRequired;

    /// <summary>
    /// A number as the person typed it: a point as the decimal separator, or a single comma where the current culture
    /// writes decimals with one.
    /// </summary>
    /// <remarks>⚠ A comma read as a decimal everywhere sends "1,000" typed on an English keyboard as 1; read nowhere, it
    /// refuses a French "7,5". Never in a whole number, which has no decimals to write. A default is written with a point,
    /// which every culture's reader takes.</remarks>
    internal static double? ParseNumber(string text, bool decimalComma)
    {
        text = Typed(text);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)) return n;
        if (decimalComma && text.Count(c => c == ',') == 1 && !text.Contains('.')
            && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n))
            return n;
        return null;
    }

    /// <summary>The current culture writes decimals with a comma.</summary>
    private static bool DecimalComma => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == ",";

    /// <summary>A typed number with what a keyboard or a culture puts in it that the invariant reader does not take: the
    /// minus sign (U+2212, which Swedish, Finnish and Norwegian write), and no-break spaces between groups of digits.</summary>
    private static string Typed(string text) =>
        text.Trim().Replace('\u2212', '-').Replace("\u00A0", "", StringComparison.Ordinal).Replace("\u202F", "", StringComparison.Ordinal);

    /// <summary>RFC 3339 date-times, seconds required: a server that checks the format refuses one without them.</summary>
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private static bool Fits(McpTextFormat format, string text) => format switch
    {
        McpTextFormat.Email    => MailAddress.TryCreate(text, out var mail) && mail.Address == text && !text.Any(char.IsWhiteSpace),
        // ⚠ "C:\x" is an absolute URI to .NET (file:///C:/x): an address is one whose text starts with its own scheme.
        McpTextFormat.Uri      => Uri.TryCreate(text, UriKind.Absolute, out var uri)
                                  && text.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase),
        McpTextFormat.Date     => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        McpTextFormat.DateTime => DateTimeOffset.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                                  && (text.EndsWith('Z') || text.EndsWith('z') || text.Length > 6 && text[^6] is '+' or '-'),
        _                      => true,
    };

    private static string FormatError(McpTextFormat format) => format switch
    {
        McpTextFormat.Email    => Strings.InputErrEmail,
        McpTextFormat.Uri      => Strings.InputErrUri,
        McpTextFormat.Date     => Strings.InputErrDate,
        _                      => Strings.InputErrDateTime,
    };

    private static string? TextHint(McpInputField f) => f.Format switch
    {
        McpTextFormat.Email    => Strings.InputHintEmail,
        McpTextFormat.Uri      => Strings.InputHintUri,
        McpTextFormat.Date     => Strings.InputHintDate,
        McpTextFormat.DateTime => Strings.InputHintDateTime,
        _                      => LengthText(f),
    };

    private static string? LengthText(McpInputField f) => (f.MinLength, f.MaxLength) switch
    {
        ({ } a, { } b) => Strings.InputLengthBetween(a, b),
        ({ } a, null)  => Strings.InputLengthAtLeast(a),
        (null, { } b)  => Strings.InputLengthAtMost(b),
        _              => null,
    };

    private static string? RangeText(McpInputField f, bool whole) => (f.Minimum, f.Maximum) switch
    {
        ({ } a, { } b) => whole ? Strings.InputRangeWhole(Shown(a), Shown(b)) : Strings.InputRangeNumber(Shown(a), Shown(b)),
        ({ } a, null)  => Strings.InputAtLeast(Shown(a)),
        (null, { } b)  => Strings.InputAtMost(Shown(b)),
        _              => whole ? Strings.InputWhole : null,
    };

    private static string? ChoiceCount(McpInputField f) => (f.MinItems, f.MaxItems) switch
    {
        ({ } a, { } b) when a == b => Strings.InputChooseExactly(a),
        ({ } a, { } b)             => Strings.InputChooseBetween(a, b),
        ({ } a, null) when a > 0   => Strings.InputChooseAtLeast(a),
        (_, { } b)                 => Strings.InputChooseAtMost(b),
        _                          => null,
    };

    /// <summary>A bound as the person reads it: with the current culture's separator, as every number the interface
    /// writes (<see cref="CountedForms"/>).</summary>
    private static string Shown(double n) => n.ToString("0.##########", CultureInfo.CurrentCulture);

    // ── JSON helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static int? Int(JsonElement e, string name) =>
        Num(e, name) is { } d && d >= 0 && d <= int.MaxValue && d == Math.Floor(d) ? (int)d : null;
}
