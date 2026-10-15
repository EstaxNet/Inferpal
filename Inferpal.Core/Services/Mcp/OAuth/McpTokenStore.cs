using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Mcp.OAuth;

/// <summary>Persisted OAuth state for one MCP server: the dynamic-registration result plus the current
/// tokens. Mutable for JSON (de)serialization.</summary>
internal sealed class McpOAuthState
{
    /// <summary>The authorization server that issued the client id and the tokens: a registered client is reused only
    /// with it.</summary>
    [JsonPropertyName("issuer")]        public string? Issuer { get; set; }
    [JsonPropertyName("clientId")]      public string? ClientId { get; set; }
    [JsonPropertyName("clientSecret")]  public string? ClientSecret { get; set; }
    [JsonPropertyName("accessToken")]   public string? AccessToken { get; set; }
    [JsonPropertyName("refreshToken")]  public string? RefreshToken { get; set; }
    [JsonPropertyName("expiresAtUtc")]  public DateTimeOffset? ExpiresAtUtc { get; set; }
    [JsonPropertyName("tokenEndpoint")] public string? TokenEndpoint { get; set; }
    [JsonPropertyName("resource")]      public string? Resource { get; set; }
    [JsonPropertyName("scopes")]        public List<string>? Scopes { get; set; }

    /// <summary>
    /// Was this state obtained for <paramref name="resource"/> (the server's canonical URL)?
    /// </summary>
    /// <remarks>
    /// ⚠ The store is keyed by the server's NAME, which the user can point at another URL (or reuse for another server):
    /// read by name alone, the token obtained for one server was sent to the other as its bearer, a refresh asked the
    /// first server's authorization server, and its registered client id was offered to the second one's — a sign-in
    /// that could never succeed. A state without a recorded resource proves nothing and is not used either.
    /// </remarks>
    public bool BelongsTo(string resource) => string.Equals(Resource, resource, StringComparison.Ordinal);

    /// <summary>True when an access token exists and isn't within <paramref name="skew"/> of expiry.</summary>
    public bool HasUsableAccessToken(TimeSpan skew) =>
        !string.IsNullOrEmpty(AccessToken)
        && (ExpiresAtUtc is null || ExpiresAtUtc.Value - skew > DateTimeOffset.UtcNow);
}

/// <summary>
/// Encrypted at-rest store for MCP OAuth state, keyed by server name. The backing file is protected
/// with Windows DPAPI (per-user) by default; the protect/unprotect functions are injectable so the
/// (de)serialization logic can be unit-tested without DPAPI. Never throws on read — a missing or
/// unreadable file gives no stored state — and never writes over an unreadable file (see <see cref="Writable"/>).
/// </summary>
internal sealed class McpTokenStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly object _lock = new();
    private Dictionary<string, McpOAuthState>? _cache;

    /// <summary>
    /// True when this store can actually encrypt: Windows DPAPI, or an injected platform secret
    /// store. Callers check it <b>before</b> starting an interactive authorization — otherwise the
    /// user completes the whole browser flow and only then hits the failure, losing the token.
    /// </summary>
    public bool CanProtect { get; }

    public McpTokenStore(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _path      = path;
        _protect   = protect   ?? DefaultProtect;
        _unprotect = unprotect ?? DefaultUnprotect;
        CanProtect = protect is not null || OperatingSystem.IsWindows();
    }

    // Windows: DPAPI per-user. On other OSes the defaults FAIL LOUD on purpose: silently
    // storing OAuth refresh tokens in cleartext would be a security regression nobody sees.
    // A non-Windows host must inject a platform secret store (e.g. VS Code SecretStorage)
    // through the ctor's protect/unprotect parameters; the throw fires on the first token
    // save, exactly where that injection was forgotten.
    private static byte[] DefaultProtect(byte[] data) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException(
                "McpTokenStore's default protection is Windows DPAPI; non-Windows hosts must inject protect/unprotect (platform secret store).");

    private static byte[] DefaultUnprotect(byte[] data) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException(
                "McpTokenStore's default protection is Windows DPAPI; non-Windows hosts must inject protect/unprotect (platform secret store).");

    /// <summary>Default location: <c>%AppData%/Inferpal/mcp-oauth.dat</c>, alongside the config file.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inferpal", "mcp-oauth.dat");

    public McpOAuthState? Get(string serverName)
    {
        lock (_lock) { return Load().TryGetValue(serverName, out var s) ? s : null; }
    }

    public void Save(string serverName, McpOAuthState state)
    {
        lock (_lock) { var map = Writable(); map[serverName] = state; Persist(map); }
    }

    public void Remove(string serverName)
    {
        lock (_lock) { var map = Load(); if (map.Remove(serverName)) Persist(map); }
    }

    /// <summary>Removes the sign-in of every server not named in <paramref name="keep"/>; returns the names removed.</summary>
    public IReadOnlyList<string> RemoveAllExcept(IReadOnlySet<string> keep)
    {
        lock (_lock)
        {
            var map  = Load();
            var gone = map.Keys.Where(name => !keep.Contains(name)).ToList();
            foreach (var name in gone) map.Remove(name);
            if (gone.Count > 0) Persist(map);
            return gone;
        }
    }

    // ⚠ The file is shared by Visual Studio and the VS Code host: the cache is only valid while the file
    // is still the one it was read from. Rewritten whole from a stale cache, a save erased the token the
    // other editor had just stored — or put back a refresh token the server had already rotated.
    private (DateTime WriteUtc, long Length) _cacheStamp;

    private (DateTime WriteUtc, long Length) CurrentStamp()
    {
        var info = new FileInfo(_path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
    }

    /// <summary>The file exists and could not be read: its sign-ins are UNKNOWN, never "none".</summary>
    private bool _unreadable;

    /// <summary>
    /// The map a write starts from: the file's — read once more when it could not be read (the other editor may have been
    /// replacing it) — or, when it still cannot be, an empty map once the file is kept aside.
    /// </summary>
    /// <remarks>
    /// ⚠ An unreadable file read as empty, then rewritten whole with the one sign-in being saved, erased every other
    /// server's sign-in — for a file another user, a locked keychain or the other editor's write in progress made
    /// unreadable for a moment. Kept aside, those sign-ins are still on disk; the copy's name is said in /diagnostics.
    /// </remarks>
    private Dictionary<string, McpOAuthState> Writable()
    {
        var map = Load();
        if (!_unreadable) return map;
        map = Load(again: true);
        if (!_unreadable) return map;

        var aside = $"{_path}.unreadable-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
        // A copy that cannot be made leaves the file as it is: the save fails with that cause rather than erase it.
        File.Copy(_path, aside, overwrite: true);
        Diagnostics.Record("Mcp",
            $"The stored MCP sign-ins could not be read; the file was kept as {Path.GetFileName(aside)} before a sign-in was " +
            "saved. The other servers ask for a sign-in again.");
        return [];
    }

    private Dictionary<string, McpOAuthState> Load(bool again = false)
    {
        var stamp = CurrentStamp();
        if (!again && _cache is not null && stamp == _cacheStamp) return _cache;
        _unreadable = false;
        try
        {
            if (File.Exists(_path))
            {
                var json = Encoding.UTF8.GetString(_unprotect(File.ReadAllBytes(_path)));
                _cache = JsonSerializer.Deserialize<Dictionary<string, McpOAuthState>>(json, JsonOpts) ?? [];
            }
            else _cache = [];
        }
        catch (Exception ex)
        {
            // Corrupt/undecryptable file (e.g. different user): no sign-in can be read from it, but it is not empty —
            // a write keeps it aside first (Writable).
            Diagnostics.Swallow("McpTokenStoreLoad", ex);
            _cache = [];
            _unreadable = true;
        }
        _cacheStamp = stamp;
        return _cache;
    }

    private void Persist(Dictionary<string, McpOAuthState> map)
    {
        _cache = map;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = _protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map, JsonOpts)));
        Services.Persistence.AtomicFile.WriteAllBytes(_path, bytes);
        _cacheStamp = CurrentStamp();
    }
}
