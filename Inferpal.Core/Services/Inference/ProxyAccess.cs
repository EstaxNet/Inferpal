using System.Text;

namespace Inferpal.Services.Inference;

/// <summary>How a reverse proxy in front of the model server is passed: what the settings offer to pick.</summary>
internal enum ProxyMethod { None, Basic, Bearer, Cloudflare, Custom }

/// <summary>The fields of one method, as the settings show them; only the method's own fields are read.</summary>
internal sealed record ProxyAccessForm(
    ProxyMethod Method,
    string User = "",
    string Password = "",
    string Token = "",
    string ClientId = "",
    string ClientSecret = "",
    string Raw = "");

/// <summary>
/// The method a <c>requestHeaders</c> text is, and back: the settings build the headers for the person (a username and a
/// password, a token, Cloudflare's service token) and the saved setting stays the text itself — one storage, read by
/// <see cref="ServerHeaders"/>, whatever form wrote it.
/// </summary>
/// <remarks>⚠ <see cref="Read"/> takes a method only when the text is EXACTLY what that method writes: anything more (a
/// second header, a comment) is <see cref="ProxyMethod.Custom"/>, so switching forms never drops a line someone wrote.
/// The VS Code settings read it again in TypeScript (<c>settingsEditors.ts</c>, <c>proxyAccessOf</c>), same rule.</remarks>
internal static class ProxyAccess
{
    internal const string CfId = "CF-Access-Client-Id";
    internal const string CfSecret = "CF-Access-Client-Secret";

    /// <summary>The method and fields <paramref name="text"/> holds.</summary>
    internal static ProxyAccessForm Read(string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0) return new(ProxyMethod.None);
        var lines = raw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var pairs = lines.Select(Split).ToList();
        if (pairs.Any(p => p is null)) return new(ProxyMethod.Custom, Raw: raw);

        if (pairs.Count == 1 && pairs[0]!.Value.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var value = pairs[0]!.Value.Value;
            if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && DecodeBasic(value[6..].Trim()) is var (user, pass))
                return new(ProxyMethod.Basic, User: user, Password: pass, Raw: raw);
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && value[7..].Trim().Length > 0)
                return new(ProxyMethod.Bearer, Token: value[7..].Trim(), Raw: raw);
        }
        if (pairs.Count == 2
            && pairs.FirstOrDefault(p => p!.Value.Name.Equals(CfId, StringComparison.OrdinalIgnoreCase)) is { } id
            && pairs.FirstOrDefault(p => p!.Value.Name.Equals(CfSecret, StringComparison.OrdinalIgnoreCase)) is { } secret)
            return new(ProxyMethod.Cloudflare, ClientId: id.Value, ClientSecret: secret.Value, Raw: raw);
        return new(ProxyMethod.Custom, Raw: raw);
    }

    /// <summary>The <c>requestHeaders</c> text of <paramref name="form"/>: its method's fields; empty when they are.</summary>
    internal static string Write(ProxyAccessForm form) => form.Method switch
    {
        ProxyMethod.Basic when form.User.Trim().Length > 0 || form.Password.Length > 0
            => "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(form.User.Trim() + ":" + form.Password)),
        ProxyMethod.Bearer when form.Token.Trim().Length > 0
            => "Authorization: Bearer " + form.Token.Trim(),
        // Both or nothing: one line alone is no Cloudflare token, and would read back as Other headers.
        ProxyMethod.Cloudflare when form.ClientId.Trim().Length > 0 && form.ClientSecret.Trim().Length > 0
            => $"{CfId}: {form.ClientId.Trim()}\n{CfSecret}: {form.ClientSecret.Trim()}",
        ProxyMethod.Custom => form.Raw.Trim(),
        _ => string.Empty,
    };

    private static (string Name, string Value)? Split(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return null;
        var value = line[(colon + 1)..].Trim();
        return value.Length == 0 ? null : (line[..colon].Trim(), value);
    }

    private static (string User, string Password)? DecodeBasic(string encoded)
    {
        try
        {
            // Strict: credentials that are not UTF-8 stay Other headers, never decoded wrong and re-encoded corrupted.
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(encoded));
            var colon = text.IndexOf(':');
            return colon < 0 ? null : (text[..colon], text[(colon + 1)..]);
        }
        catch (FormatException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }
}
