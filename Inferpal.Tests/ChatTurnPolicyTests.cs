using System;
using System.Collections.Generic;
using System.Linq;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure send-pipeline logic extracted from the tool-window VM: the
// empty-bubble triple guard, the final-answer fallback chain, tool summaries and
// previews, the context-enriched history text, the multi-file recap inputs, the
// persisted-answer choice, and the prompt-history rules. UI insertion, theming,
// and the LLM calls stay in the VM and are not tested here.
public class ChatTurnPolicyTests
{
    // ── OneLinePreview / FormatPromptHistory (/phistory) ───────────────────────

    [Theory]
    [InlineData("short",            "short")]
    [InlineData("two\nlines",       "two lines")]
    public void OneLinePreview_FlattensNewlines(string text, string expected) =>
        Assert.Equal(expected, ChatTurnPolicy.OneLinePreview(text, 80));

    [Fact]
    public void OneLinePreview_CapsWithEllipsis() =>
        Assert.Equal("aaaaa…", ChatTurnPolicy.OneLinePreview(new string('a', 9), 5));

    [Fact]
    public void FormatPromptHistory_MostRecentFirst_WithUseCommands()
    {
        var listing = ChatTurnPolicy.FormatPromptHistory(["first", "second"], term: null);
        Assert.NotNull(listing);
        Assert.StartsWith(Strings.PHistoryListHeader, listing);
        var idxSecond = listing!.IndexOf("**#2** second  `/phistory use 2`", StringComparison.Ordinal);
        var idxFirst  = listing.IndexOf("**#1** first  `/phistory use 1`", StringComparison.Ordinal);
        Assert.True(idxSecond >= 0 && idxFirst > idxSecond); // reversed order
    }

    [Fact]
    public void FormatPromptHistory_FiltersByTerm_CaseInsensitive()
    {
        var listing = ChatTurnPolicy.FormatPromptHistory(["fix the Bug", "add feature"], "bug");
        Assert.NotNull(listing);
        Assert.Contains("fix the Bug", listing);
        Assert.DoesNotContain("add feature", listing);
        Assert.Contains("`bug`", listing); // term echoed in the header
    }

    [Fact]
    public void FormatPromptHistory_NoMatch_GivesNull() =>
        Assert.Null(ChatTurnPolicy.FormatPromptHistory(["hello"], "nope"));

    // ── IsVisiblyEmpty (triple guard, see bug_empty_bubble_history) ────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n")]
    [InlineData("​﻿‌")]                       // invisible Unicode only
    [InlineData("<think>long reasoning</think>")]            // think-only output
    [InlineData("<think>reasoning</think>---")]              // the Qwen final-agent-answer artefact
    [InlineData("---")]                                      // separator renders as 1-px line
    [InlineData("---\n\n---")]                               // separators only
    public void IsVisiblyEmpty_True_ForInvisibleContent(string? content) =>
        Assert.True(ChatTurnPolicy.IsVisiblyEmpty(content));

    [Theory]
    [InlineData("Hello")]
    [InlineData("<think>reasoning</think>The answer is 42.")]
    [InlineData("---\nReal text after a separator")]
    [InlineData("```cs\nvar x = 1;\n```")]
    public void IsVisiblyEmpty_False_ForVisibleContent(string content) =>
        Assert.False(ChatTurnPolicy.IsVisiblyEmpty(content));

    // ── DecideFinalAnswer (fallback chain) ─────────────────────────────────────

    [Fact]
    public void DecideFinalAnswer_PrefersStreamedBubble() =>
        Assert.Equal(FinalAnswerKind.StreamedAnswer,
            ChatTurnPolicy.DecideFinalAnswer(streamingBubbleVisible: true, "ignored", 3));

    [Fact]
    public void DecideFinalAnswer_FallsBackToFinalText_WhenVisible() =>
        Assert.Equal(FinalAnswerKind.FinalText,
            ChatTurnPolicy.DecideFinalAnswer(false, "<think>r</think>The answer.", 0));

    [Fact]
    public void DecideFinalAnswer_FallsBackToToolSummary_WhenFinalIsThinkOnly() =>
        Assert.Equal(FinalAnswerKind.ToolSummary,
            ChatTurnPolicy.DecideFinalAnswer(false, "<think>r</think>---", 2));

    [Fact]
    public void DecideFinalAnswer_EmptyFallback_WhenNothingVisibleAndNoTools() =>
        Assert.Equal(FinalAnswerKind.EmptyFallback,
            ChatTurnPolicy.DecideFinalAnswer(false, "", 0));

    // ── BuildToolSummary ───────────────────────────────────────────────────────

    [Fact]
    public void BuildToolSummary_GroupsRepeatedToolsWithCount()
    {
        var summary = ChatTurnPolicy.BuildToolSummary(
        [
            new ToolExecution("read_file",  "a", "out"),
            new ToolExecution("write_file", "b", "out"),
            new ToolExecution("read_file",  "c", "out"),
        ]);

        Assert.Equal("read_file ×2, write_file", summary);
    }

    // ── BuildToolPreview ───────────────────────────────────────────────────────

    [Fact]
    public void BuildToolPreview_KeepsShortOutputIntact() =>
        Assert.Equal("short", ChatTurnPolicy.BuildToolPreview("short"));

    [Fact]
    public void BuildToolPreview_TruncatesLongOutput()
    {
        var preview = ChatTurnPolicy.BuildToolPreview(new string('x', 600));

        Assert.StartsWith(new string('x', 500), preview);
        Assert.True(preview.Length < 600);
        Assert.NotEqual(new string('x', 600), preview);
    }

    // ── BuildHistoryText ───────────────────────────────────────────────────────

    [Fact]
    public void BuildHistoryText_NoAttachments_ReturnsUserTextAsIs() =>
        Assert.Equal("question", ChatTurnPolicy.BuildHistoryText("question", []));

    [Fact]
    public void BuildHistoryText_WrapsAttachmentsInLabelledFences()
    {
        var att  = new AttachmentItem("Foo.cs", "var x = 1;", onRemove: () => { });
        var text = ChatTurnPolicy.BuildHistoryText("explain", [att]);

        Assert.Contains("[Attached: Foo.cs]", text);
        Assert.Contains("```", text);
        Assert.Contains("var x = 1;", text);
        Assert.EndsWith("explain", text);
    }

    // ── ModifiedFilePaths ──────────────────────────────────────────────────────

    [Fact]
    public void ModifiedFilePaths_FiltersWriteToolsWithDiff_AndDeduplicates()
    {
        var diffA = new DiffInfo("old", "new", @"C:\a.cs");
        var diffB = new DiffInfo("old", "new", @"C:\b.cs");
        var paths = ChatTurnPolicy.ModifiedFilePaths(
        [
            new ToolExecution("write_file", "a", "ok", Diff: diffA),
            new ToolExecution("apply_diff", "b", "ok", Diff: diffB),
            new ToolExecution("write_file", "a", "ok", Diff: diffA),   // duplicate path
            new ToolExecution("read_file",  "c", "ok"),                // not a write tool
            new ToolExecution("write_file", "d", "ok"),                // write without diff
        ]);

        Assert.Equal([@"C:\a.cs", @"C:\b.cs"], paths);
    }

    // ── ChoosePersistedAnswer ──────────────────────────────────────────────────

    [Fact]
    public void ChoosePersistedAnswer_PrefersShownBubble_AndStripsThink() =>
        Assert.Equal("shown",
            ChatTurnPolicy.ChoosePersistedAnswer("<think>r</think>shown", "stored final"));

    [Fact]
    public void ChoosePersistedAnswer_FallsBackToFinalResponse() =>
        Assert.Equal("stored final",
            ChatTurnPolicy.ChoosePersistedAnswer(null, "stored final"));

    [Fact]
    public void ChoosePersistedAnswer_Empty_WhenNothingSurvivesStripping() =>
        Assert.Equal(string.Empty,
            ChatTurnPolicy.ChoosePersistedAnswer(null, "<think>only thoughts</think>"));

    // ── AppendPromptHistory ────────────────────────────────────────────────────

    [Fact]
    public void AppendPromptHistory_AppendsAndReportsChange()
    {
        var history = new List<string> { "a" };

        Assert.True(ChatTurnPolicy.AppendPromptHistory(history, "b", max: 10));
        Assert.Equal(["a", "b"], history);
    }

    [Fact]
    public void AppendPromptHistory_SkipsConsecutiveDuplicate()
    {
        var history = new List<string> { "a" };

        Assert.False(ChatTurnPolicy.AppendPromptHistory(history, "a", max: 10));
        Assert.Equal(["a"], history);
    }

    [Fact]
    public void AppendPromptHistory_EvictsOldest_PastMax()
    {
        var history = new List<string> { "a", "b", "c" };

        Assert.True(ChatTurnPolicy.AppendPromptHistory(history, "d", max: 3));
        Assert.Equal(["b", "c", "d"], history);
    }
}
