using System.Globalization;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Import;

/// <summary>One line of the import card: an item the user can tick.</summary>
/// <param name="Number">1-based, as <c>/import … apply 1,3</c> names it.</param>
internal sealed record ImportCardItem(int Number, string Target, string Label, bool Checked, IReadOnlyList<string> Notes);

/// <summary>Parts left out for one reason, with the paths of the source they are.</summary>
internal sealed record ImportCardGroup(string Reason, IReadOnlyList<string> Paths);

/// <summary>
/// The import preview, every text already in the interface's language: what the two editors draw (a card with a box per
/// item) and what the Markdown version says (the agent protocol, the conversation's record).
/// </summary>
/// <param name="Source">The command's keyword (<c>continue</c>, <c>roo</c>): the card's apply runs <c>/import</c> with it.</param>
/// <param name="File">The file read: applying reads it again.</param>
/// <param name="SkippedTitle">The fold's title with its count, the summary's number: editors show it as is.</param>
/// <param name="Fingerprint">What the card shows, item by item (<see cref="ImportPreview.Fingerprint"/>): its Apply sends it
/// with the numbers, and an application whose plan no longer matches writes nothing.</param>
internal sealed record ImportCard(string Source, string File, string Title, string Summary, IReadOnlyList<ImportCardItem> Items,
                                  string SkippedTitle, IReadOnlyList<ImportCardGroup> Skipped, string ApplyLabel,
                                  string ApplyHint, string Fingerprint);

/// <summary>Draws an <see cref="ImportPlan"/> for people: the card, its Markdown, and what an application did.</summary>
internal static class ImportPreview
{
    public static string Keyword(ImportSource source) => source == ImportSource.Continue ? "continue" : "roo";

    public static string ToolName(ImportSource source) => source == ImportSource.Continue ? "Continue" : "Roo Code";

    public static ImportCard Card(ImportPlan plan)
    {
        var items = plan.Items.Select((item, i) => new ImportCardItem(
            i + 1, Target(item.Kind), Label(item.Effect), item.Checked, [.. item.Notes.Select(Note)])).ToList();
        // A detail that qualifies the reason (the hosted provider, the other server, the role) stays in it, one line per
        // value; a detail that only names the element (a context provider, a Roo setting) goes beside its path — grouped
        // by it, a Roo export made some forty one-line groups.
        var groups = plan.Skipped
            .GroupBy(s => (s.Cause, Detail: QualifiesTheReason(s.Cause) ? s.Detail : null))
            .Select(g => new ImportCardGroup(
                Reason(g.Key.Cause) + (g.Key.Detail is { } d ? " (" + d + ")" : ""),
                [.. g.Select(s => g.Key.Detail is null && s.Detail is { } own && !s.Path.EndsWith(own, StringComparison.Ordinal)
                                  ? s.Path + " (" + own + ")"
                                  : s.Path)
                     .Distinct(StringComparer.Ordinal)]))
            .ToList();
        // Counted once, by path: one part can be left out for two reasons (a model's roles — one already held, one Inferpal
        // does not have) and so sit in two groups. The summary and the fold's title say this same number; an editor
        // that summed the groups would print another one under it.
        var skippedParts = plan.Skipped.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count();
        var summary = items.Count == 0
            ? Strings.ImportNothingToWrite
            : Strings.ImportPreviewSummary(items.Count, skippedParts);
        var skippedTitle = Strings.ImportSkippedTitle + " (" + skippedParts.ToString(CultureInfo.InvariantCulture) + ")";
        return new ImportCard(Keyword(plan.Source), plan.File, Strings.ImportPreviewTitle(ToolName(plan.Source), plan.File),
                              summary, items, skippedTitle, groups, Strings.ImportApplyButton,
                              Strings.ImportApplyHint(Keyword(plan.Source)), Fingerprint(plan));
    }

    /// <summary>The plan's items in order — each one's id and exactly what it would write — as a short hash.</summary>
    /// <remarks>
    /// ⚠ A card's boxes are NUMBERS into a plan that <c>apply</c> reads again, and that plan moves: an item applied since
    /// the preview is "already in place" and leaves the list, every later item moves up one. Read by number, the box ticked
    /// on line 3 writes line 4 — an <c>allow</c> rule the card showed unticked. The hash is of what the card is drawn from,
    /// so a card applied against another plan is refused instead of being read against it.
    /// </remarks>
    public static string Fingerprint(ImportPlan plan)
    {
        var text = new StringBuilder();
        foreach (var item in plan.Items)
            text.Append(item.Id).Append('\u001f')
                .Append(System.Text.Json.JsonSerializer.Serialize(item.Effect, item.Effect.GetType())).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>The card as Markdown: a numbered list with the boxes as <c>[x]</c>, the reasons, how to apply.</summary>
    public static string Markdown(ImportCard card)
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(card.Title).Append("**\n\n").Append(card.Summary).Append("\n\n");
        foreach (var item in card.Items)
        {
            sb.Append(item.Number.ToString(CultureInfo.InvariantCulture)).Append(". [").Append(item.Checked ? 'x' : ' ')
              .Append("] **").Append(item.Target).Append("** — ").Append(item.Label).Append('\n');
            foreach (var note in item.Notes) sb.Append("   _").Append(note).Append("_\n");
        }
        if (card.Skipped.Count > 0)
        {
            sb.Append("\n**").Append(card.SkippedTitle).Append("**\n\n");
            foreach (var group in card.Skipped)
            {
                var shown = group.Paths.Take(6).Select(p => "`" + p + "`");
                var more = group.Paths.Count > 6 ? " (+" + (group.Paths.Count - 6).ToString(CultureInfo.InvariantCulture) + ")" : "";
                sb.Append("- ").Append(group.Reason).Append(" — ").Append(string.Join(", ", shown)).Append(more).Append('\n');
            }
        }
        if (card.Items.Count > 0) sb.Append('\n').Append(card.ApplyHint);
        return sb.ToString().TrimEnd();
    }

    /// <summary>What an application did, and what comes next — the copy of the settings named first.</summary>
    public static string Outcome(ImportOutcome outcome, string commandsFolder)
    {
        if (outcome.Refused is { } refused) return Strings.ImportRefused(refused);
        if (outcome.Applied.Count == 0 && outcome.Failed.Count == 0) return Strings.ImportNothingChosen;
        var lines = new List<string> { Strings.ImportApplied(outcome.Applied.Count) };
        // What was written, by name: the card above keeps the ticks it was drawn with, and once the conversation is saved
        // only its text comes back — a box the user cleared reads ticked again. This list is the record of the decision.
        if (outcome.Applied.Count > 0)
            lines.Add(string.Join("\n", outcome.Applied.Select(i => "- " + Target(i.Kind) + " — " + Label(i.Effect))));
        lines.AddRange(outcome.Failed.Select(f => Strings.ImportItemFailed(Label(f.Item.Effect), f.Reason)));
        if (outcome.Backup is { } backup) lines.Add(Strings.ImportBackup(backup));
        if (outcome.Applied.Any(i => i.Kind == ImportItemKind.Command)) lines.Add(Strings.ImportCommandsWhere(commandsFolder));
        if (outcome.Applied.Any(i => i.Kind == ImportItemKind.DocSite)) lines.Add(Strings.ImportDocsNext);
        if (outcome.ServerChanged) lines.Add(Strings.ImportRestart);
        return string.Join("\n\n", lines);
    }

    /// <summary>Whether a skip's detail says something about the reason (shared by the paths) or only names the element.</summary>
    private static bool QualifiesTheReason(ImportSkipCause cause) => cause is
        ImportSkipCause.HostedProvider or ImportSkipCause.OtherServer or ImportSkipCause.SecretNotFound
        or ImportSkipCause.OneModelPerRole or ImportSkipCause.RoleNotInInferpal or ImportSkipCause.SameModelAsChat
        or ImportSkipCause.AlreadyConfigured or ImportSkipCause.IndexingNotKept or ImportSkipCause.ActiveMode
        or ImportSkipCause.UnsupportedLanguage;

    // ── Texts ───────────────────────────────────────────────────────────────

    public static string Label(ImportEffect effect) => effect switch
    {
        SetBackend b         => b.Provider + " · " + b.BaseUrl,
        SetModel m           => m.Model,
        SetContextWindow w   => w.Tokens.ToString("N0", CultureInfo.CurrentCulture),
        AddInstructions i    => i.Title,
        WriteCommand c       => "/" + c.Name + (string.IsNullOrWhiteSpace(c.Description) ? "" : " — " + c.Description),
        AddMcpServer s       => s.Server.Name + " (" + (s.Server.Command ?? s.Server.Url) + ")",
        AddDocSite d         => d.Title == d.StartUrl ? d.StartUrl : d.Title + " (" + d.StartUrl + ")",
        AddPermissionRule r  => r.Rule,
        SetLanguage l        => l.Code,
        SetIndexing x        => x.Enabled ? Strings.ImportIndexingOn : Strings.ImportIndexingOff,
        _                    => effect.GetType().Name,
    };

    private static string Target(ImportItemKind kind) => kind switch
    {
        ImportItemKind.Backend         => Strings.ImportTargetServer,
        ImportItemKind.ChatModel       => Strings.ImportTargetChatModel,
        ImportItemKind.CompletionModel => Strings.ImportTargetCompletionModel,
        ImportItemKind.EditModel       => Strings.ImportTargetEditModel,
        ImportItemKind.UtilityModel    => Strings.ImportTargetUtilityModel,
        ImportItemKind.EmbeddingModel  => Strings.ImportTargetEmbeddingModel,
        ImportItemKind.ContextWindow   => Strings.ImportTargetContextWindow,
        ImportItemKind.Instructions    => Strings.ImportTargetInstructions,
        ImportItemKind.Command         => Strings.ImportTargetCommand,
        ImportItemKind.McpServer       => Strings.ImportTargetMcpServer,
        ImportItemKind.DocSite         => Strings.ImportTargetDocSite,
        ImportItemKind.PermissionRule  => Strings.ImportTargetPermissionRule,
        ImportItemKind.Language        => Strings.ImportTargetLanguage,
        ImportItemKind.Indexing        => Strings.ImportTargetIndexing,
        _                              => kind.ToString(),
    };

    private static string Note(ImportItemNote note) => note.Kind switch
    {
        ImportNote.Renamed                 => Strings.ImportNoteRenamed(note.Detail ?? ""),
        ImportNote.IgnoredByContinue       => Strings.ImportNoteIgnoredByContinue(note.Detail ?? ""),
        ImportNote.ScopeInText             => Strings.ImportNoteScopeInText,
        ImportNote.ModeToolsNotKept        => Strings.ImportNoteModeToolsNotKept,
        ImportNote.TakesEffectAfterRestart => Strings.ImportNoteTakesEffectAfterRestart,
        ImportNote.SecretToFill            => Strings.ImportNoteSecretToFill(note.Detail ?? ""),
        ImportNote.KeyNotCarried           => Strings.ImportNoteKeyNotCarried(note.Detail ?? ""),
        ImportNote.RunsWithoutAsking       => Strings.ImportNoteRunsWithoutAsking(note.Detail ?? ""),
        ImportNote.ContinueDefault         => Strings.ImportNoteContinueDefault,
        _                                  => note.Kind.ToString(),
    };

    private static string Reason(ImportSkipCause cause) => cause switch
    {
        ImportSkipCause.FileIdentity           => Strings.ImportSkipFileIdentity,
        ImportSkipCause.HostedProvider         => Strings.ImportSkipHostedProvider,
        ImportSkipCause.OtherServer            => Strings.ImportSkipOtherServer,
        ImportSkipCause.OtherChatModel         => Strings.ImportSkipOtherChatModel,
        ImportSkipCause.Autodetect             => Strings.ImportSkipAutodetect,
        ImportSkipCause.SameModelAsChat        => Strings.ImportSkipSameModelAsChat,
        ImportSkipCause.OneModelPerRole        => Strings.ImportSkipOneModelPerRole,
        ImportSkipCause.RoleNotInInferpal      => Strings.ImportSkipRoleNotInInferpal,
        ImportSkipCause.DisplayName            => Strings.ImportSkipDisplayName,
        ImportSkipCause.Sampling               => Strings.ImportSkipSampling,
        ImportSkipCause.ResponseLength         => Strings.ImportSkipResponseLength,
        ImportSkipCause.DeclaredCapabilities   => Strings.ImportSkipDeclaredCapabilities,
        ImportSkipCause.RequestOptions         => Strings.ImportSkipRequestOptions,
        ImportSkipCause.AutocompleteOptions    => Strings.ImportSkipAutocompleteOptions,
        ImportSkipCause.PromptTemplates        => Strings.ImportSkipPromptTemplates,
        ImportSkipCause.ContextProviders       => Strings.ImportSkipContextProviders,
        ImportSkipCause.HubBlock               => Strings.ImportSkipHubBlock,
        ImportSkipCause.Telemetry              => Strings.ImportSkipTelemetry,
        ImportSkipCause.SecretNotFound         => Strings.ImportSkipSecretNotFound,
        ImportSkipCause.ContinueBuiltinCommand => Strings.ImportSkipContinueBuiltinCommand,
        ImportSkipCause.AlreadyConfigured      => Strings.ImportSkipAlreadyConfigured,
        ImportSkipCause.Empty                  => Strings.ImportSkipEmpty,
        ImportSkipCause.UnknownKey             => Strings.ImportSkipUnknownKey,
        ImportSkipCause.NoEquivalent           => Strings.ImportSkipNoEquivalent,
        ImportSkipCause.ModelPerMode           => Strings.ImportSkipModelPerMode,
        ImportSkipCause.ActiveMode             => Strings.ImportSkipActiveMode,
        ImportSkipCause.AutoApproval           => Strings.ImportSkipAutoApproval,
        ImportSkipCause.AutoApprovalWasOff     => Strings.ImportSkipAutoApprovalWasOff,
        ImportSkipCause.Compaction             => Strings.ImportSkipCompaction,
        ImportSkipCause.Checkpoints            => Strings.ImportSkipCheckpoints,
        ImportSkipCause.IndexingNotKept        => Strings.ImportSkipIndexingNotKept,
        ImportSkipCause.ToolCatalogue          => Strings.ImportSkipToolCatalogue,
        ImportSkipCause.WhenToUse              => Strings.ImportSkipWhenToUse,
        ImportSkipCause.UnsupportedLanguage    => Strings.ImportSkipUnsupportedLanguage,
        ImportSkipCause.NoServersInExport      => Strings.ImportSkipNoServersInExport,
        _                                      => cause.ToString(),
    };
}
