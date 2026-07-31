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
    [JsonPropertyName("clientId")]      public string? ClientId { get; set; }
    [JsonPropertyName("clientSecret")]  public string? ClientSecret { get; set; }
    [JsonPropertyName("accessToken")]   public string? AccessToken { get; set; }
    [JsonPropertyName("refreshToken")]  public string? RefreshToken { get; set; }
    [JsonPropertyName("expiresAtUtc")]  public DateTimeOffset? ExpiresAtUtc { get; set; }
    [JsonPropertyName("tokenEndpoint")] public string? TokenEndpoint { get; set; }
    [JsonPropertyName("resource")]      public string? Resource { get; set; }
    [JsonPropertyName("scopes")]        public List<string>? Scopes { get; set; }

    /// <summary>True when an access token exists and isn't within <paramref name="skew"/> of expiry.</summary>
    public bool HasUsableAccessToken(TimeSpan skew) =>
        !string.IsNullOrEmpty(AccessToken)
        && (ExpiresAtUtc is null || ExpiresAtUtc.Value - skew > DateTimeOffset.UtcNow);
}

/// <summary>
/// Encrypted at-rest store for MCP OAuth state, keyed by server name. The backing file is protected
/// with Windows DPAPI (per-user) by default; the protect/unprotect functions are injectable so the
/// (de)serialization logic can be unit-tested without DPAPI. Never throws on read — a missing or
/// unreadable file is treated as "no stored state".
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
        lock (_lock) { var map = Load(); map[serverName] = state; Persist(map); }
    }

    public void Remove(string serverName)
    {
        lock (_lock) { var map = Load(); if (map.Remove(serverName)) Persist(map); }
    }

    private Dictionary<string, McpOAuthState> Load()
    {
        if (_cache is not null) return _cache;
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
            // Corrupt/undecryptable file (e.g. different user) → start fresh rather than crash,
            // but leave a trace: silently losing every stored token is hard to diagnose otherwise.
            Diagnostics.Swallow("McpTokenStoreLoad", ex);
            _cache = [];
        }
        return _cache;
    }

    private void Persist(Dictionary<string, McpOAuthState> map)
    {
        _cache = map;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = _protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map, JsonOpts)));
        Services.Persistence.AtomicFile.WriteAllBytes(_path, bytes);
    }
}
