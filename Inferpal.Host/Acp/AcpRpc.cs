using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Services;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Inferpal.Host.Acp;

/// <summary>
/// The connection to an Agent Client Protocol client: JSON-RPC 2.0, one message per line on stdio, camelCase names,
/// absent rather than null optional fields.
/// </summary>
/// <remarks>
/// ⚠ Lines end in LF alone, and nothing but ACP goes to stdout (Program.cs reroutes <c>Console.Out</c> to stderr): the
/// registry's check fails an agent that writes anything else there.
/// </remarks>
internal static class AcpRpc
{
    /// <summary>ACP's error codes (schema <c>ErrorCode</c>).</summary>
    internal const int InvalidParams = -32602, InternalError = -32603, AuthRequired = -32000, NotFound = -32002;

    public static JsonRpc Create(Stream sending, Stream receiving, object target)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy        = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull;

        var handler = new NewLineDelimitedMessageHandler(sending, receiving, formatter)
        {
            NewLine = NewLineDelimitedMessageHandler.NewLineStyle.Lf,
        };
        var rpc = new AcpJsonRpc(handler);
        rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions { DisposeOnDisconnect = false });
        // ⚠ No $/cancelRequest of our own: ACP's clients do not know it, and a request we stop waiting for (a
        // permission the turn no longer needs) is simply abandoned.
        rpc.CancellationStrategy = null;
        return rpc;
    }

    /// <summary>An error the client reads: ACP's code, a single-line message.</summary>
    public static LocalRpcException Error(int code, string message) =>
        new(OneLine(message)) { ErrorCode = code };

    /// <summary>
    /// An error message is "a concise single sentence": a multi-line message (a server's reply, a stack) is folded onto one
    /// line so that a client which draws the first line only still shows the cause.
    /// </summary>
    internal static string OneLine(string message)
    {
        var folded = string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return folded.Length == 0 ? "Request failed." : folded;
    }
}

/// <summary>
/// <see cref="JsonRpc"/> with ACP's error mapping.
/// </summary>
/// <remarks>
/// ⚠ StreamJsonRpc answers an exception thrown by a handler with <c>-32000</c> and the exception's details in
/// <c>data</c> — and in ACP <c>-32000</c> is "authentication required": a client then offers to sign in for a failure
/// that has nothing to do with it. Every error is mapped here: ours keep their code, a bad argument is
/// <c>-32602</c>, anything else <c>-32603</c>, never with a stack.
/// </remarks>
internal sealed class AcpJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
{
    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        var root = exception;
        while (root is TargetInvocationException { InnerException: { } inner }) root = inner;
        while (root is AggregateException { InnerExceptions.Count: 1 } agg) root = agg.InnerExceptions[0];

        var (code, message) = root switch
        {
            LocalRpcException local                        => (local.ErrorCode, local.Message),
            ArgumentException or JsonException or FormatException => (AcpRpc.InvalidParams, root.Message),
            _                                              => (AcpRpc.InternalError, root.Message),
        };
        if (root is not LocalRpcException) Diagnostics.Swallow($"Acp.{request.Method}", root);
        return new JsonRpcError.ErrorDetail { Code = (JsonRpcErrorCode)code, Message = AcpRpc.OneLine(message) };
    }
}
