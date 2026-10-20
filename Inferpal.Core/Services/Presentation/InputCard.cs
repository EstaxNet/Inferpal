using System.Text;
using System.Text.Json.Nodes;
using Inferpal.Localization;
using Inferpal.Services.Mcp;

namespace Inferpal.Services.Presentation;

/// <summary>One choice of a field as the card draws it.</summary>
internal sealed record InputOptionModel(string Value, string Label, bool Selected);

/// <summary>One field of an input card, in the person's language, with the value it holds and what to fix in it.</summary>
/// <param name="Kind"><c>text</c>, <c>number</c>, <c>boolean</c>, <c>choice</c> (one) or <c>choices</c> (several).</param>
/// <param name="InputType">For a <c>text</c> or <c>number</c> field, what the editor's box takes: <c>text</c>,
/// <c>email</c>, <c>url</c>, <c>date</c>, <c>datetime</c> or <c>number</c>.</param>
/// <param name="Buttons">A <c>choice</c> field with few short choices, drawn as buttons side by side; otherwise a list.</param>
/// <param name="Warns">The field reads like a secret: <paramref name="Hint"/> says a server must not ask for one.</param>
/// <param name="Error">What to fix before sending; empty when the field passes or was not checked yet.</param>
internal sealed record InputFieldModel(
    string Key, string Kind, string InputType, string Label, string Hint, bool Required, bool Warns, bool Buttons,
    string Text, bool Flag, IReadOnlyList<InputOptionModel> Options, string Error, double? Min, double? Max);

/// <summary>A form an MCP server asks the person to fill, as a card in the chat — the same card in both editors.</summary>
/// <param name="Id">The same for every showing of one question: a card drawn again with what to fix replaces the first.</param>
/// <param name="Message">The server's own words: drawn as plain text, never Markdown, never a link.</param>
/// <param name="ErrorSummary">"2 answers to fix before sending"; empty on a first showing.</param>
internal sealed record InputCardModel(
    string Id, string Server, string Title, string Subtitle, string Message, IReadOnlyList<InputFieldModel> Fields,
    string ErrorSummary, string RequiredNote, string Reminder,
    string SendLabel, string DeclineLabel, string DeclineTooltip, string CancelLabel, string CancelTooltip);

/// <summary>What the person did on an input card, as the editor sends it back.</summary>
/// <param name="Values">The fields' values when they sent them; empty otherwise.</param>
internal sealed record InputReply(McpInputAction Action, IReadOnlyDictionary<string, McpInputValue> Values);

/// <summary>
/// The line an answered card leaves in the conversation, kept with it: what the person did, and what they sent.
/// </summary>
/// <param name="Body">The values sent, one per line, and to whom they went; empty when nothing was sent.</param>
internal sealed record InputReceipt(string CardId, string Title, string Detail, string Body)
{
    /// <summary>The receipt as the conversation saves it (role <see cref="InputCard.ReceiptRole"/>): the title, the
    /// detail, then the body.</summary>
    public string Saved => Title + "\n" + Detail + (Body.Length > 0 ? "\n" + Body : string.Empty);

    /// <summary>A receipt read back from a saved conversation (<see cref="Saved"/>).</summary>
    public static InputReceipt FromSaved(string saved)
    {
        var lines = saved.Replace("\r\n", "\n").Split('\n', 3);
        return new InputReceipt(string.Empty, lines[0], lines.Length > 1 ? lines[1] : string.Empty, lines.Length > 2 ? lines[2] : string.Empty);
    }
}

/// <summary>Builds <see cref="InputCardModel"/> and <see cref="InputReceipt"/>.</summary>
internal static class InputCard
{
    /// <summary>The role an answered card's receipt is saved under. The card itself is screen only: a card that waits
    /// is never in a saved conversation.</summary>
    public const string ReceiptRole = "input";

    /// <summary>The role of the card itself while it waits, in a chat list (<see cref="Persistence.SessionManager.ScreenOnlyRoles"/>).</summary>
    public const string CardRole = "inputcard";

    /// <summary>Up to this many choices, each no longer than <see cref="ShortChoice"/>, are buttons side by side.</summary>
    private const int ButtonChoices = 4;
    private const int ShortChoice   = 18;

    public static InputCardModel Build(McpInputForm form, string id, IReadOnlyDictionary<string, McpInputValue> values,
                                       IReadOnlyDictionary<string, string> errors)
    {
        var fields = form.Fields.Select(f => Field(f, values.TryGetValue(f.Key, out var v) ? v : null,
                                                   errors.TryGetValue(f.Key, out var e) ? e : string.Empty)).ToList();
        var toFix = fields.Count(f => f.Error.Length > 0);
        return new InputCardModel(
            id, form.Server,
            Strings.InputCardTitle(form.Server), Strings.InputCardSubtitle(form.Tool), form.Message, fields,
            toFix > 0 ? Strings.InputFixCount(toFix) : string.Empty,
            fields.Any(f => f.Required) ? Strings.InputCardRequired : string.Empty,
            Strings.InputCardReminder,
            Strings.InputSend, Strings.InputDecline, Strings.InputDeclineTip(form.Server),
            Strings.InputCancel, Strings.InputCancelTip(form.Server));
    }

    private static InputFieldModel Field(McpInputField f, McpInputValue? v, string error)
    {
        var warns = McpInputForm.LooksSecret(f);
        // A field to fix shows what to fix instead of what it takes: shown together, they say the same thing twice.
        var hint  = McpInputForm.Hint(f, takes: error.Length == 0);
        if (warns) hint = hint.Length > 0 ? hint + " · " + Strings.InputSecretWarning : Strings.InputSecretWarning;
        var (kind, input) = f.Kind switch
        {
            McpInputKind.Number or McpInputKind.Integer => ("number", "number"),
            McpInputKind.Boolean                        => ("boolean", ""),
            McpInputKind.Choice                         => ("choice", ""),
            McpInputKind.Choices                        => ("choices", ""),
            _ => ("text", f.Format switch
            {
                McpTextFormat.Email    => "email",
                McpTextFormat.Uri      => "url",
                McpTextFormat.Date     => "date",
                McpTextFormat.DateTime => "datetime",
                _                      => "text",
            }),
        };
        var picked = f.Kind == McpInputKind.Choices ? v?.Picks ?? [] : v?.Text is { } one ? [one] : [];
        var options = f.Choices.Select(o => new InputOptionModel(o.Value, o.Label, picked.Contains(o.Value))).ToList();
        var buttons = f.Kind == McpInputKind.Choice && options.Count <= ButtonChoices && options.All(o => o.Label.Length <= ShortChoice);
        return new InputFieldModel(f.Key, kind, input, f.Label, hint, f.Required, warns, buttons,
                                   f.Kind is McpInputKind.Choice or McpInputKind.Choices ? string.Empty : v?.Text ?? string.Empty,
                                   v?.Flag ?? false, options, error, f.Minimum, f.Maximum);
    }

    /// <summary>What an answered card leaves in the conversation.</summary>
    /// <param name="content">The values sent, for <see cref="McpInputAction.Accept"/>.</param>
    public static InputReceipt Receipt(McpInputForm form, string id, McpInputAction action, JsonObject? content)
    {
        switch (action)
        {
            case McpInputAction.Accept:
                var lines = new StringBuilder();
                var sent = 0;
                foreach (var f in form.Fields)
                {
                    if (content?[f.Key] is not { } value) continue;
                    sent++;
                    // ⚠ The receipt is saved with the conversation: a value that reads like a secret is sent, never kept.
                    lines.Append(f.Label).Append(": ").Append(McpInputForm.LooksSecret(f) ? HiddenValue : Shown(f, value)).Append('\n');
                }
                lines.Append(Strings.InputReceiptSentNote(form.Server));
                return new InputReceipt(id, Strings.InputReceiptSent(form.Server), Strings.InputReceiptSentDetail(sent), lines.ToString());
            case McpInputAction.Decline:
                return new InputReceipt(id, Strings.InputReceiptDeclined(form.Server), Strings.InputReceiptNothingSent, string.Empty);
            case McpInputAction.SetAside:
                return new InputReceipt(id, Strings.InputReceiptSetAside(form.Server), Strings.InputReceiptNothingSent, string.Empty);
            default:
                return new InputReceipt(id, Strings.InputReceiptCancelled(form.Server), Strings.InputReceiptNothingSent, string.Empty);
        }
    }

    /// <summary>What a receipt shows for a value that reads like a secret.</summary>
    internal const string HiddenValue = "••••••";

    /// <summary>What a card leaves when the run was stopped while it waited.</summary>
    public static InputReceipt Stopped(McpInputForm form, string id) =>
        new(id, Strings.InputReceiptStopped(form.Server), Strings.InputReceiptUnanswered, string.Empty);

    /// <summary>A sent value as the person chose it: a choice by its label, a box as yes or no.</summary>
    private static string Shown(McpInputField f, JsonNode value)
    {
        string Label(string v) => f.Choices.FirstOrDefault(o => o.Value == v)?.Label ?? v;
        return f.Kind switch
        {
            McpInputKind.Boolean => value.GetValue<bool>() ? Strings.InputYes : Strings.InputNo,
            McpInputKind.Choice  => Label(value.GetValue<string>()),
            McpInputKind.Choices => string.Join(", ", value.AsArray().Select(n => Label(n!.GetValue<string>()))),
            McpInputKind.Number or McpInputKind.Integer => value.ToJsonString(),
            _                    => value.GetValue<string>().Replace('\n', ' '),
        };
    }
}
