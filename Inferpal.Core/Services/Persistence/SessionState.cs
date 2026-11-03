namespace Inferpal.Services.Persistence;

/// <summary>
/// What a conversation holds besides its messages, declared once: every front-end captures it in one place and applies
/// it in one place, and a session file keeps all of it (<see cref="SessionData.State"/>). The defaults are a new
/// conversation's.
/// </summary>
/// <remarks>
/// ⚠ Loading a conversation REPLACES every value, a session written before a field existed included (it reads as the
/// default): applied field by field, a load left the previous conversation's Plan mode, tools, step mode, X-Ray sections
/// and plan file in place, under a conversation that never had them.
/// </remarks>
/// <param name="CurrentName">The named session the conversation lives in (the auto-save slot records it).</param>
/// <param name="TemplateSuffix">The <c>/template</c> mode the conversation is held in.</param>
/// <param name="Mode">The custom mode the person picked for it; <c>null</c> for Chat, Agent or Plan.</param>
/// <param name="PlanMode">Plan mode: read-only tools, a plan before any change.</param>
/// <param name="ToolsOff"><c>/tools off</c>: the model answers without tools.</param>
/// <param name="StepMode">Step mode: a run pauses before each call.</param>
/// <param name="XrayOff">The prompt sections turned off in X-Ray, by name.</param>
/// <param name="ActivePlan">The <c>/plan</c> file the conversation follows.</param>
/// <param name="Summary">What the model's history holds in place of the conversation's first turns, once compacted.</param>
/// <param name="Recap">The session recap written every few turns, the newest.</param>
/// <param name="RecapInSystem">The recap the system message carries: the one written before the last compaction.</param>
/// <param name="Model">The model the conversation was put on by an editor that picks one per conversation (an Agent
/// Client Protocol thread). Visual Studio and VS Code answer with their window's model: they keep this one, never use it.</param>
internal sealed record SessionState(
    string? CurrentName = null,
    string? TemplateSuffix = null,
    string? Mode = null,
    bool PlanMode = false,
    bool ToolsOff = false,
    bool StepMode = false,
    IReadOnlyList<string>? XrayOff = null,
    string? ActivePlan = null,
    Agent.HistorySummary? Summary = null,
    string? Recap = null,
    string? RecapInSystem = null,
    string? Model = null)
{
    /// <summary>A new conversation's state.</summary>
    public static SessionState New { get; } = new();

    /// <summary>The sections turned off, never <c>null</c>.</summary>
    public IReadOnlyList<string> XrayOffSections => XrayOff ?? [];

    /// <summary>The state a branch starts with: the conversation's, without what was written of its turns — a summary and
    /// a recap tell of turns the branch may not have. Its first question measures the branch again.</summary>
    /// <remarks>A method: a property returning a state would be printed by the record's <c>ToString</c>, which prints its
    /// own, without end.</remarks>
    public SessionState ForBranch() => this with { Summary = null, Recap = null, RecapInSystem = null };

    /// <inheritdoc/>
    /// <remarks>Compares the sections by content: two captures of one state are equal.</remarks>
    public bool Equals(SessionState? other) =>
        other is not null
        && CurrentName == other.CurrentName && TemplateSuffix == other.TemplateSuffix && Mode == other.Mode
        && PlanMode == other.PlanMode && ToolsOff == other.ToolsOff && StepMode == other.StepMode
        && XrayOffSections.SequenceEqual(other.XrayOffSections, StringComparer.Ordinal)
        && ActivePlan == other.ActivePlan && Summary == other.Summary && Recap == other.Recap
        && RecapInSystem == other.RecapInSystem && Model == other.Model;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(CurrentName, TemplateSuffix, Mode, PlanMode, ToolsOff, StepMode, XrayOffSections.Count, ActivePlan),
        Summary, Recap, RecapInSystem, Model);
}
