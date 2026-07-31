using Inferpal.Services.Persistence;

namespace Inferpal.Services.Commands;

/// <summary>
/// Outcome of <c>/template</c>. Either a plain message (the list, or an unknown id), or the
/// template to apply — the caller clears the conversation and installs
/// <see cref="SessionTemplate.SystemSuffix"/> in its own way (the VS VM rebuilds its system
/// prompt in place, the host resets its history and tells the adapter to clear the transcript).
/// </summary>
internal sealed record TemplateCommandResult(string? Message = null, SessionTemplate? Apply = null);

/// <summary>
/// Execution logic for <c>/template [id]</c> — listing the presets and resolving one. The
/// side effect (clearing the conversation, installing the suffix) stays with the caller because
/// it is genuinely different on each front-end; the decision does not have to be.
/// </summary>
internal static class TemplateCommandHandler
{
    public static TemplateCommandResult Handle(string[] parts)
    {
        if (parts.Length < 2)
            return new TemplateCommandResult(SessionManager.FormatTemplateList());

        var id   = parts[1].ToLowerInvariant();
        var tmpl = SessionManager.FindTemplate(id);

        return tmpl is null
            ? new TemplateCommandResult($"Unknown template `{id}`. Type `/template` to see the list.")
            : new TemplateCommandResult(Apply: tmpl);
    }
}
