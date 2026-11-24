using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Databases;

/// <summary>A connection string read where the project declares it, and where that is, as the card and the steps say it.</summary>
/// <param name="Reference">The place as the agent named it (<c>appsettings.Development.json#ConnectionStrings:Default</c>).</param>
/// <param name="Shown">The place as a person reads it (<c>appsettings.Development.json › ConnectionStrings:Default</c>).</param>
/// <param name="ConnectionString">The value — never shown, never sent to the model.</param>
internal sealed record DatabaseSource(string Reference, string Shown, string ConnectionString);

/// <summary>
/// Reads a connection string where the project declares it. Inferpal stores none (pari 115): the agent names the place —
/// <c>&lt;file&gt;#&lt;key&gt;</c> (a JSON settings file, a <c>.env</c>), <c>env:&lt;NAME&gt;</c>, or
/// <c>secrets:&lt;project&gt;#&lt;key&gt;</c> (the project's .NET user secrets) — and the value is read there.
/// </summary>
/// <remarks>
/// ⚠ The agent passes the place, never the string: the password is not copied into each call, its history and the model's
/// server. It is no boundary — the agent may read the file — only no copy. ⚠ A file kept from the model (the ignore files)
/// stays kept here too, and a file outside the workspace is not read.
/// </remarks>
internal static class DatabaseSources
{
    /// <summary>Where the .NET user secrets of a project live; replaced by tests.</summary>
    internal static Func<string> UserSecretsHome { get; set; } = () => OperatingSystem.IsWindows()
        ? Path.Combine(UserFolders.Of(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(UserFolders.Of(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SecretsId = new(@"<UserSecretsId>\s*([^<\s]+)\s*</UserSecretsId>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>The connection string <paramref name="reference"/> names, or why it cannot be read (said to the model).</summary>
    public static async Task<(DatabaseSource? Source, string? Refusal)> ResolveAsync(string reference, string? root, CancellationToken ct)
    {
        reference = reference.Trim();
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = reference[4..].Trim();
            if (name.Length == 0) return (null, "env: names no variable (env:DATABASE_URL).");
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? Found(reference, $"the environment variable {name}", value)
                : (null, $"The environment variable {name} is not set in the environment Inferpal runs in.");
        }
        var hash = reference.LastIndexOf('#');
        if (hash <= 0 || hash == reference.Length - 1)
            return (null, $"'{reference}' names no place: write <file>#<key> (appsettings.Development.json#ConnectionStrings:Default, " +
                          ".env#DATABASE_URL), env:<NAME>, or secrets:<project file>#<key>.");
        var (where, key) = (reference[..hash].Trim(), reference[(hash + 1)..].Trim());
        if (where.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
            return await UserSecretsAsync(reference, where[8..].Trim(), key, root, ct);

        if (Resolve(where, root, out var path) is { } refused) return (null, refused);
        if (!File.Exists(path)) return (null, $"No file {where} in the workspace.");
        var text = await Tools.TextFileEncoding.ReadTextAsync(path, ct);
        var name2 = Path.GetFileName(path);
        string? found;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (JsonKey(text, key, out found, out var near) is { } bad) return (null, $"{where}: {bad}");
            if (found is null) return (null, $"{where} has no key {key}{near}.");
        }
        else if (name2.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name2.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            found = EnvKey(text, key);
            if (found is null) return (null, $"{where} has no line {key}=.");
        }
        else return (null, $"{where}: only a JSON settings file or a .env file is read for a connection string.");
        return Found(reference, $"{Relative(path, root)} › {key}", found);
    }

    private static (DatabaseSource?, string?) Found(string reference, string shown, string value)
    {
        value = value.Trim();
        if (value.Length == 0) return (null, $"{shown} is empty.");
        // A value the app fills at start (${DB_PASSWORD}, %DB%) is not a connection string yet.
        if (value.Contains("${", StringComparison.Ordinal) || value.Contains("{{", StringComparison.Ordinal))
            return (null, $"{shown} holds a placeholder the app fills when it starts: name where the value itself is (env:…).");
        return (new DatabaseSource(reference, shown, value), null);
    }

    /// <summary>The workspace file <paramref name="where"/> names, or why it may not be read.</summary>
    private static string? Resolve(string where, string? root, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(root)) return "No folder is open: a connection is read in the project's own files.";
        try { path = Tools.PathSanitizer.Sanitize(where, root); }
        catch (ArgumentException ex) { return ex.Message; }
        if (!RunRoot.Holds(Path.GetFullPath(root), path)) return $"{where} is outside the workspace: a connection is read in the project's own files.";
        if (ContentExclusions.Of(path) is { } excluded) return excluded.ForModel(where);
        return null;
    }

    private static async Task<(DatabaseSource?, string?)> UserSecretsAsync(string reference, string project, string key, string? root, CancellationToken ct)
    {
        if (Resolve(project, root, out var file) is { } refused) return (null, refused);
        if (Directory.Exists(file))
            file = Directory.EnumerateFiles(file, "*.csproj").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() ?? file;
        if (!File.Exists(file) || !file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return (null, $"secrets: names a project file (Shop.Api/Shop.Api.csproj); {project} is none.");
        if (SecretsId.Match(await Tools.TextFileEncoding.ReadTextAsync(file, ct)) is not { Success: true } id)
            return (null, $"{project} declares no <UserSecretsId>: it has no user secrets.");
        // ⚠ The repository wrote the id: a name, never a path out of the user-secrets folder (..\, C:\elsewhere).
        if (!System.Text.RegularExpressions.Regex.IsMatch(id.Groups[1].Value, @"^[A-Za-z0-9][A-Za-z0-9_.\-]*$", System.Text.RegularExpressions.RegexOptions.None, MatchTimeout)
            || id.Groups[1].Value.Contains("..", StringComparison.Ordinal))
            return (null, $"{project} declares a <UserSecretsId> that is no plain name: its user secrets are not read.");
        var secrets = Path.Combine(UserSecretsHome(), id.Groups[1].Value, "secrets.json");
        if (!File.Exists(secrets)) return (null, $"The user secrets of {project} hold nothing yet (dotnet user-secrets set …).");
        var text = await Tools.TextFileEncoding.ReadTextAsync(secrets, ct);
        if (JsonKey(text, key, out var found, out var near) is { } bad) return (null, $"The user secrets of {project}: {bad}");
        if (found is null) return (null, $"The user secrets of {project} have no key {key}{near}.");
        return Found(reference, $"the user secrets of {Path.GetFileNameWithoutExtension(file)} › {key}", found);
    }

    /// <summary>The string at <paramref name="key"/> — a .NET configuration path (<c>ConnectionStrings:Default</c>), nested
    /// or written flat as one name, as user secrets are; <paramref name="near"/> names the keys beside it when it is missing.</summary>
    internal static string? JsonKey(string json, string key, out string? found, out string near)
    {
        found = null;
        near = string.Empty;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { return $"it is not valid JSON ({ex.Message.Split('.')[0]})"; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "it is not a JSON object";
            if (Property(doc.RootElement, key) is { ValueKind: JsonValueKind.String } flat) { found = flat.GetString(); return null; }
            var node = doc.RootElement;
            var parts = key.Split(':');
            for (var i = 0; i < parts.Length; i++)
            {
                if (node.ValueKind != JsonValueKind.Object || Property(node, parts[i]) is not { } child)
                {
                    var prefix = i == 0 ? string.Empty : string.Join(":", parts.Take(i)) + ":";
                    var names = node.ValueKind == JsonValueKind.Object ? node.EnumerateObject().Select(p => prefix + p.Name).Take(12).ToList() : [];
                    if (names.Count > 0) near = $" (beside it: {string.Join(", ", names)})";
                    return null;
                }
                node = child;
            }
            if (node.ValueKind != JsonValueKind.String) return $"{key} is not a string";
            found = node.GetString();
            return null;
        }
    }

    // .NET configuration keys ignore case.
    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    /// <summary>The value of <c>KEY=value</c> in a .env file: <c>export</c> allowed, quotes removed, a comment after an unquoted
    /// value cut.</summary>
    internal static string? EnvKey(string text, string key) => DotEnv.Parse(text).TryGetValue(key, out var value) ? value : null;

    private static string Relative(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return Path.GetFileName(path);
        var rel = Path.GetRelativePath(root, path);
        return rel.Replace('\\', '/');
    }
}
