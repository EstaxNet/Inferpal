using System.IO;
using System.Text.Json;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// Builds the host's <see cref="JsonRpc"/> connections with the wire conventions the
/// TypeScript side expects: header-delimited framing (vscode-jsonrpc native) and camelCase
/// property names both ways (TS parameter objects bind case-insensitively onto the DTOs).
/// Shared by Program.cs and the headless protocol tests so both speak the exact same wire.
/// </summary>
internal static class HostRpc
{
    public static JsonRpc Create(Stream sending, Stream receiving, object? target = null)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy        = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;

        var handler = new HeaderDelimitedMessageHandler(sending, receiving, formatter);
        return target is null ? new JsonRpc(handler) : new JsonRpc(handler, target);
    }
}
