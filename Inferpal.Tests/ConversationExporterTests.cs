using System;
using System.Collections.Generic;
using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure export-document formatting extracted from the tool-window VM:
// the shared stats header, the .txt layout (underlined headers) and the markdown
// layout (stats table, "##" headers), and the duration formatting. The snapshot
// capture, save dialog, and file write stay in the VM and are not tested here.
// Serialized: the header tests switch Strings.OverrideCulture, which is process-wide.
[Collection(CultureSerialCollection.Name)]
public class ConversationExporterTests
{
    private static readonly List<ExportMessage> Sample =
    [
        new("user",      "You",       "hello",       "10:00"),
        new("tool",      "read_file", "file content", ""),
        new("assistant", "Ollama",    "hi there",    "10:01"),
        new("user",      "You",       "thanks",      "10:02"),
    ];

    private static string Build(bool asPlainText, string language = "en")
    {
        try
        {
            Strings.ApplyLanguage(language);
            return ConversationExporter.Build(Sample, asPlainText,
                modelName: "qwen3:8b", sessionTokens: 42, date: "date-here", durationStr: "3m 4s");
        }
        finally { Strings.ApplyLanguage(null); }
    }

    // ── FormatDuration ─────────────────────────────────────────────────────────

    [Fact]
    public void FormatDuration_NoStartRecorded_GivesDash() =>
        Assert.Equal("—", ConversationExporter.FormatDuration(null));

    [Fact]
    public void FormatDuration_MinutesAndSeconds() =>
        Assert.Equal("62m 5s",
            ConversationExporter.FormatDuration(new TimeSpan(1, 2, 5)));

    // ── Markdown layout ────────────────────────────────────────────────────────

    [Fact]
    public void Markdown_HasStatsTableAndHeaders()
    {
        var doc = Build(asPlainText: false);
        Assert.Contains("# Inferpal — Conversation Export", doc);
        Assert.Contains("*date-here*", doc);
        Assert.Contains("| Model | `qwen3:8b` |", doc);
        Assert.Contains("| Turns | 2 |", doc);       // user messages only
        Assert.Contains("| Tool calls | 1 |", doc);
        Assert.Contains("| Tokens | 42 |", doc);
        Assert.Contains("| Duration | 3m 4s |", doc);
        Assert.Contains("## You (10:00)", doc);
        Assert.Contains("hi there", doc);
    }

    [Fact]
    public void Markdown_OmitsTimestampParens_WhenTimestampEmpty()
    {
        var doc = Build(asPlainText: false);
        Assert.Contains("## read_file\n", doc.Replace("\r\n", "\n"));
        Assert.DoesNotContain("read_file (", doc);
    }

    // ── Plain-text layout ──────────────────────────────────────────────────────

    [Fact]
    public void PlainText_HasStatsLineAndUnderlinedHeaders()
    {
        var doc = Build(asPlainText: true);
        Assert.Contains("Inferpal — Conversation Export", doc);
        Assert.Contains("Model: qwen3:8b  |  Turns: 2  |  Tool calls: 1  |  Tokens: 42  |  Duration: 3m 4s", doc);
        // Header underlined with dashes of the same length: "You (10:00)" = 11 chars.
        Assert.Contains("You (10:00)\n" + new string('-', 11), doc.Replace("\r\n", "\n"));
        Assert.DoesNotContain("##", doc);
    }

    // ── Translated header ──────────────────────────────────────────────────────

    // The document header is read by the same person as the turn labels, which go through
    // Strings: an English title and stats table above French turns is a half-translated
    // document. Expectations are literals: reading the resource would turn the test green
    // on a header left hard-coded.
    [Fact]
    public void Markdown_Header_FollowsTheLanguage()
    {
        var doc = Build(asPlainText: false, language: "fr");
        Assert.Contains("# Inferpal — Export de conversation", doc);
        Assert.Contains("| Statistique | Valeur |", doc);
        Assert.Contains("| Modèle | `qwen3:8b` |", doc);
        Assert.Contains("| Tours | 2 |", doc);
        Assert.Contains("| Appels d’outils | 1 |", doc);
        Assert.Contains("| Tokens | 42 |", doc);
        Assert.Contains("| Durée | 3m 4s |", doc);
        Assert.DoesNotContain("Conversation Export", doc);
    }

    [Fact]
    public void PlainText_Header_FollowsTheLanguage()
    {
        var doc = Build(asPlainText: true, language: "fr");
        Assert.Contains("Inferpal — Export de conversation", doc);
        Assert.Contains("Modèle: qwen3:8b  |  Tours: 2  |  Appels d’outils: 1  |  Tokens: 42  |  Durée: 3m 4s", doc);
        Assert.DoesNotContain("Model:", doc);
    }
}
