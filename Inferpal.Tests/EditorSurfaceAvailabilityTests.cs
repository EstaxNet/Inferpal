using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the tools say about the editor when they cannot see it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IEditorSurface.IsAvailable"/> states its own contract: <i>"Distinct from 'no active
/// document': tools use it to tell the user how to recover instead of claiming no file is open."</i>
/// Two sites out of three honoured it — <c>get_active_document</c> and the write gate behind
/// <c>insert_at_cursor</c>/<c>replace_selection</c>. The third was <c>get_open_editors</c>, the one
/// tool whose entire job is to answer that question, and it asserted <i>"No files are currently
/// open in the editor. The user may need to open a file first."</i>
/// </para>
/// <para>
/// Under Visual Studio the surface is genuinely unavailable until an <c>IClientContext</c> has been
/// captured, so the model was told the user had nothing open — and advised them to open a file that
/// already was. Same defect as <c>get_debugger_state</c> answering "No paused debug session" to a
/// VS Code user stopped at a breakpoint: not a missing capability, a <b>false answer</b>.
/// </para>
/// <para>
/// The four remaining consumers of the surface (git status, solution info, memory, project map) use
/// the open paths as a <i>hint</i> and fall back elsewhere when it is empty. They assert nothing to
/// the model, so they need no check — that distinction, assertion versus hint, is the whole rule.
/// </para>
/// </remarks>
public class EditorSurfaceAvailabilityTests
{
    private static JsonElement NoArgs() => JsonDocument.Parse("{}").RootElement;

    /// <summary>A surface that has not been handed a usable editor context.</summary>
    private sealed class UnavailableSurface : IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>A working surface with nothing open — the case that IS "no file is open".</summary>
    private sealed class AvailableButEmptySurface : IEditorSurface
    {
        public bool IsAvailable => true;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
            Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
            Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task GetOpenEditors_SaysTheContextIsMissing_NotThatNothingIsOpen()
    {
        var result = await new GetOpenEditorsTool(new UnavailableSurface())
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.Equal(Strings.ActiveDocNoContext, result);
        // The sentence the model must NOT read here: it is an assertion about the user's editor,
        // and it is false whenever the surface is the thing that is missing.
        Assert.DoesNotContain("No files are currently open", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetActiveDocument_SaysTheSameThing_ForTheSameReason()
    {
        // The site that already held the contract. Kept as a witness: if this one ever stops
        // checking, the rule above is no longer describing a shared discipline.
        var result = await new GetActiveDocumentTool(new UnavailableSurface())
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.Equal(Strings.ActiveDocNoContext, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheTwoEditorToolsNeverGiveOppositeAnswersAboutTheSameSession(bool available)
    {
        // What the defect really cost: in the same session, one tool said "no context" and the
        // other said "nothing is open". The model has no way to prefer the true one.
        IEditorSurface surface = available ? new AvailableButEmptySurface() : new UnavailableSurface();

        var open   = await new GetOpenEditorsTool(surface).ExecuteAsync(NoArgs(), CancellationToken.None);
        var active = await new GetActiveDocumentTool(surface).ExecuteAsync(NoArgs(), CancellationToken.None);

        var openBlamesContext   = open.Contains(Strings.ActiveDocNoContext, StringComparison.Ordinal);
        var activeBlamesContext = active.Contains(Strings.ActiveDocNoContext, StringComparison.Ordinal);

        Assert.Equal(activeBlamesContext, openBlamesContext);
        Assert.Equal(!available, openBlamesContext);
    }

    [Fact]
    public async Task AnAvailableSurfaceWithNothingOpen_StillSaysNothingIsOpen()
    {
        // The counterpart: the message the fix must NOT swallow. "No file is open" is the right
        // answer when the editor is reachable and empty.
        var result = await new GetOpenEditorsTool(new AvailableButEmptySurface())
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.Contains("No files are currently open", result, StringComparison.Ordinal);
    }
}
