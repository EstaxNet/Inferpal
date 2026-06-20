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

    public McpTokenStore(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _path      = path;
        _protect   = protect   ?? (data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
    }

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
        catch
        {
            // Corrupt/undecryptable file (e.g. different user) → start fresh rather than crash.
            _cache = [];
        }
        return _cache;
    }

    private void Persist(Dictionary<string, McpOAuthState> map)
    {
        _cache = map;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = _protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map, JsonOpts)));
        File.WriteAllBytes(_path, bytes);
    }
}
