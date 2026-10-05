using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A session's title — what a model wrote — is capped before it names a file.
//
//  Asked for "4 to 5 words", a small utility model can answer the message instead. Kept whole, that
//  answer became the archive's file name: past 255 characters the save failed, and /clear had
//  already cleared the conversation — "it is gone".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SessionTitleLengthTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-title-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly string AnAnswerInsteadOfATitle =
        string.Join(" ", Enumerable.Range(0, 300).Select(i => $"word{i}"));

    [Fact]
    public void ALongReply_IsCut_OnAWordBoundary()
    {
        var title = SessionManager.SanitizeTitle(AnAnswerInsteadOfATitle, "fallback");

        Assert.True(title.Length <= SessionManager.MaxTitleChars, $"{title.Length} characters");
        Assert.StartsWith("word0_word1_", title, StringComparison.Ordinal);
        // Whole words only: the last one is one the reply wrote.
        Assert.Contains(" " + title.Split('_')[^1] + " ", " " + AnAnswerInsteadOfATitle + " ", StringComparison.Ordinal);
    }

    [Fact]
    public void AShortTitle_IsKeptAsItIs()
    {
        // Reference arm: what the prompt asks for passes untouched.
        Assert.Equal("Fix_the_login_bug", SessionManager.SanitizeTitle("Fix the login bug", "fallback"));
    }

    [Fact]
    public async Task TheArchive_IsWritten_WhateverTheModelReplied()
    {
        var name = SessionManager.SessionFileName(new DateTime(2026, 10, 5, 14, 30, 0),
                                                  SessionManager.SanitizeTitle(AnAnswerInsteadOfATitle, "fallback"));

        await new ConversationStore(_dir).SaveAsync(name, [new SavedMessage("user", "hello")], CancellationToken.None);

        Assert.Single(Directory.GetFiles(_dir, "*.json"));
    }
}
