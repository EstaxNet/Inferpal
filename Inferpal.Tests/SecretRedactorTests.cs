using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a diagnostic entry loses on its way out of the machine.
/// </summary>
/// <remarks>
/// <para>
/// The support bundle is the product's only field channel — Inferpal ships no telemetry, so a user
/// reporting a problem pastes this file into a public issue. It already redacted the place a secret
/// was <i>expected</i> (the configured API key, the endpoint) and exported the diagnostics ring
/// verbatim — which is where one actually shows up: <c>Permission</c> entries carry the raw command
/// line the model wrote, <c>DocCrawler</c> and <c>McpOAuth</c> carry raw URLs.
/// </para>
/// <para>
/// The second battery below is the one that keeps this useful: a redactor that mangles ordinary
/// text costs the bundle its diagnostic value, which is the whole reason it exists.
/// </para>
/// </remarks>
public class SecretRedactorTests
{
    // ── What must never leave ────────────────────────────────────────────────

    [Theory]
    // The measured case: a header on a command line the permission layer recorded.
    [InlineData(@"curl -H ""Authorization: Bearer sk-ant-api03-abcdef123456"" https://api.example/v1",
                "sk-ant-api03-abcdef123456")]
    [InlineData("curl -H 'Authorization: Basic dXNlcjpwYXNzd29yZA==' https://x.example", "dXNlcjpwYXNzd29yZA==")]
    // Vendor shapes recognisable with no key name around them.
    [InlineData("git push https://ghp_AbCdEf0123456789AbCdEf@github.com/x/y", "ghp_AbCdEf0123456789AbCdEf")]
    [InlineData("export SLACK=xoxb-1234567890-abcdefghij", "xoxb-1234567890-abcdefghij")]
    [InlineData("aws configure set key AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    // Explicit name/value forms.
    [InlineData("gh auth login --token ghp_ZZZZZZZZZZZZZZZZZZZZ", "ghp_ZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("psql --password=hunter2correcthorse", "hunter2correcthorse")]
    [InlineData(@"{""api_key"": ""abcdef0123456789""}", "abcdef0123456789")]
    [InlineData("API_KEY=s3cr3tvalue dotnet run", "s3cr3tvalue")]
    // Credentials inside a URL, the shape McpOAuth and DocCrawler entries carry.
    [InlineData("Refused a private address: https://admin:s3cret@10.0.0.5/api", "s3cret")]
    [InlineData("https://api.example/v1/things?access_token=abc123def456&page=2", "abc123def456")]
    public void ACredential_DoesNotSurviveTheExport(string detail, string secret)
    {
        var redacted = SecretRedactor.Redact(detail);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    // ── What must survive, or the bundle stops being worth reading ───────────

    [Theory]
    [InlineData("dotnet build Inferpal.sln -c Release")]
    [InlineData("git commit -m \"fix the parser\"")]
    [InlineData("Blocked run_command: rm -rf /")]
    [InlineData("The requested operation requires an element of type 'String'")]
    [InlineData("Docs schema 3 != 4 - rebuilding the index.")]
    [InlineData("Refused a private/loopback address: http://192.168.1.10:8080/docs")]
    [InlineData("npm run typecheck")]
    // A word that merely contains "token" is not a key/value form.
    [InlineData("Dropped an orphaned tool result (no matching tool_call).")]
    [InlineData("Tokenizer produced 4096 tokens for the prompt")]
    public void OrdinaryDiagnostics_ComeThroughUntouched(string detail) =>
        Assert.Equal(detail, SecretRedactor.Redact(detail));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void EmptyInput_IsHandled(string? detail, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(detail));

    // ── The seam: export scrubs, the local view does not ─────────────────────

    [Fact]
    public void TheExportScrub_RunsTheRedactor()
    {
        const string detail = @"Blocked run_command: curl -H ""Authorization: Bearer sk-live-9876543210"" https://x";

        var exported = DiagnosticsCommandHandler.SanitizePaths(detail, workspaceRoot: null);

        Assert.DoesNotContain("sk-live-9876543210", exported, StringComparison.Ordinal);
        // The entry stays readable: what was blocked is still legible, only the value is gone.
        Assert.Contains("Blocked run_command", exported, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void PathScrubbing_StillHappens()
    {
        // The witness for the half that was already there: adding redaction must not displace it.
        var scrubbed = DiagnosticsCommandHandler.SanitizePaths(
            @"Could not read C:\work\repo\src\A.cs", workspaceRoot: @"C:\work\repo");

        Assert.Contains("<workspace>", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\work\repo\src", scrubbed, StringComparison.Ordinal);
    }
}
