using System.Text.RegularExpressions;

namespace Inferpal.Services;

/// <summary>
/// Masks credential-shaped text on its way <b>out of the machine</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The support bundle is the product's only field channel — Inferpal ships zero telemetry, so a
/// user reporting a problem pastes this file into a <b>public issue</b>. The bundle already covered
/// the place a secret was <i>expected</i> (the API key prints as "set (redacted)", a non-loopback
/// endpoint is withheld) but exported the diagnostics ring verbatim — and the ring is where a
/// secret actually shows up:
/// </para>
/// <list type="bullet">
///   <item><c>Permission</c> entries carry the <b>raw command line</b> the model wrote — the
///   force-prompt branch fires precisely on the opaque ones;</item>
///   <item><c>DocCrawler</c> and <c>McpOAuth</c> carry <b>raw URLs</b>.</item>
/// </list>
/// <para>
/// <b>Export only.</b> <c>/diagnostics list</c> still prints the truth: the user is debugging their
/// own machine and needs the real command. Redaction belongs at the seam where the text leaves —
/// the same seam <c>RedactEndpoint</c> already sits on.
/// </para>
/// <para>
/// <b>Which way the errors cost.</b> A false positive masks a token that was not one: the bundle
/// loses a few characters. A false negative publishes a credential. The patterns below therefore
/// aim at recognised credential shapes and explicit key/value forms rather than at anything that
/// looks random — and they keep the <i>shape</i> (<c>--token ***</c>) so the entry stays readable.
/// ⚠ This is a <b>best-effort scrub, not a guarantee</b>: a secret in a form nobody listed goes
/// through, exactly as the hard denylist is an anti-accident guard rather than a boundary. Never
/// describe it as making the bundle safe to publish blind.
/// </para>
/// </remarks>
internal static class SecretRedactor
{
    private const string Mask = "***";

    // ⚠ Every pattern carries a match budget: this text is written by a model and by remote
    // servers, so it is unbounded input (convention rule 2).
    private static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Authorization headers, in the shapes a shell command actually carries them.</summary>
    private static readonly Regex AuthHeader = new(
        @"(Authorization\s*:\s*(?:Bearer|Basic|Token)\s+)\S+", Opts, RegexBudget.Default);

    /// <summary>Vendor key shapes recognisable on their own, with no surrounding key name.</summary>
    private static readonly Regex VendorKey = new(
        @"\b(sk-ant-|sk-|ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|xox[abprs]-|glpat-|AIza|AKIA)[A-Za-z0-9_\-]{8,}",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>The credential-ish names, shared by the two forms below.</summary>
    private const string SecretNames =
        @"api[_-]?keys?|access[_-]?tokens?|auth[_-]?tokens?|tokens?|secrets?|"
        + @"client[_-]?secrets?|passwords?|passwd|pwd";

    /// <summary><c>--token X</c>, <c>-password X</c> — a command-line flag and its value.</summary>
    /// <remarks>
    /// ⚠ The space separator is allowed <b>only</b> after a dash-prefixed flag. My first version
    /// accepted a bare word followed by a space, which turned "produced 4096 <b>tokens for</b> the
    /// prompt" into a redaction — caught immediately by the "ordinary diagnostics survive" battery,
    /// which is what that battery is for: a redactor that mangles ordinary text costs the bundle the
    /// diagnostic value it exists to carry.
    /// </remarks>
    private static readonly Regex FlagValue = new(
        @"(?<name>--?(?:" + SecretNames + @")\b\s+)(?<value>[^\s""',;)]+)", Opts, RegexBudget.Default);

    /// <summary><c>--password=X</c>, <c>API_KEY=X</c>, <c>"secret": "X"</c> — an explicit assignment.</summary>
    private static readonly Regex AssignedValue = new(
        @"(?<name>(?:--?)?\b(?:" + SecretNames + @")\b""?\s*[:=]\s*""?)(?<value>[^\s""',;)]+)",
        Opts, RegexBudget.Default);

    /// <summary>Credentials embedded in a URL: <c>scheme://user:password@host</c>.</summary>
    private static readonly Regex UrlCredentials = new(
        @"([a-z][a-z0-9+.\-]*://[^\s/:@]+:)[^\s/@]+(@)", Opts, RegexBudget.Default);

    /// <summary>Credential-bearing query parameters.</summary>
    private static readonly Regex QuerySecret = new(
        @"([?&](?:key|token|access_token|api_key|apikey|auth|sig|signature|password)=)[^&\s""']+",
        Opts, RegexBudget.Default);

    /// <summary>
    /// Returns <paramref name="text"/> with credential-shaped runs masked. Never throws: a pattern
    /// that exhausts its budget leaves that text as it was rather than losing the whole entry.
    /// </summary>
    internal static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        // Order matters: the URL and header forms are more specific than the generic name=value one.
        text = Apply(AuthHeader,     text, m => m.Groups[1].Value + Mask);
        text = Apply(UrlCredentials, text, m => m.Groups[1].Value + Mask + m.Groups[2].Value);
        text = Apply(QuerySecret,    text, m => m.Groups[1].Value + Mask);
        text = Apply(AssignedValue,  text, m => m.Groups["name"].Value + Mask);
        text = Apply(FlagValue,      text, m => m.Groups["name"].Value + Mask);
        text = Apply(VendorKey,      text, m => m.Groups[1].Value + Mask);
        return text;
    }

    private static string Apply(Regex rx, string text, MatchEvaluator replace)
    {
        try { return rx.Replace(text, replace); }
        catch (RegexMatchTimeoutException)
        {
            // A pathological entry is not worth losing the rest of the bundle over -- but it is
            // also not worth publishing unread, so it goes out masked whole.
            Diagnostics.Swallow("SecretRedactor", new TimeoutException("redaction budget exhausted"));
            return Mask;
        }
    }
}
