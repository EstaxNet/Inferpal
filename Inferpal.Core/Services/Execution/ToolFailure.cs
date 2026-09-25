namespace Inferpal.Services.Execution;

/// <summary>
/// What a thrown tool call becomes for the <b>model</b> to read: the cause, and the only advice that
/// is true about it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The message of a WRAPPER exception names nothing.</b> A tool that fails inside a static
/// constructor — the shape a missing native SQLite library takes, so <c>search_codebase</c> — hands
/// up a <see cref="TypeInitializationException"/> whose own message is the fixed sentence
/// <i>"The type initializer for 'X' threw an exception"</i>, while the cause that could be acted on
/// sits one <c>InnerException</c> away. <see cref="Diagnostics.RootMessage"/> is the one reader of
/// that question; it unwraps the three framework wrappers and nothing else.
/// </para>
/// <para>
/// ⚠ <b>And the advice asserted a cause it did not know.</b> "Check the arguments against the tool's
/// schema and try again" is right for the common case — a small model omitting a required argument,
/// which every tool signals with an <see cref="ArgumentException"/> — and wrong for every other, where
/// it sends the model round the loop rewriting arguments that were never the problem. The
/// discriminator is the framework's own type hierarchy on the <b>root</b> exception, not a list of our
/// own names.
/// </para>
/// <para>
/// One sentence, two producers: the registry catches what a tool throws, the agent funnel catches what
/// escapes a registry (decorators, an MCP registry, <c>EmptyToolRegistry</c>). They describe the same
/// situation to the same reader.
/// </para>
/// </remarks>
internal static class ToolFailure
{
    /// <summary>The model-facing sentence for <paramref name="ex"/> thrown by <paramref name="toolName"/>.</summary>
    internal static string Describe(string toolName, Exception ex)
    {
        // An exception with no message at all (a bare NullReferenceException in some runtimes) would
        // otherwise render "failed — .": the type name is the least useless thing left to say.
        var cause = Diagnostics.RootMessage(ex).Trim();
        if (cause.Length == 0) cause = ex.GetType().Name;

        var separator = cause.EndsWith('.') ? " " : ". ";
        return $"Error: the '{toolName}' call failed — {cause}{separator}{Advice(ex)}";
    }

    private static string Advice(Exception ex) =>
        IsAboutTheArguments(ex)
            ? "Check the arguments against the tool's schema and try again."
        // ⚠ A failure of ONE file is not a failure of the tool: read-only (a TFVC or Perforce workspace keeps files
        // read-only until checked out), locked by another program, protected. "Continue without this tool" made the
        // model give up every write of its task when only that file was out of reach.
        : IsAboutOneFile(ex)
            ? "The arguments are not the cause: this file or folder cannot be accessed as asked (read-only, locked by "
              + "another program, or protected), so the same call will fail until that changes. Say what happened; "
              + "other files are not affected."
            : "The arguments are not the cause, so the same call will fail the same way: say what "
              + "happened and continue without this tool.";

    /// <summary>True when the root failure is about one file system entry rather than the tool itself.</summary>
    /// <remarks>⚠ Not when a type initializer failed on the way: a native library missing inside a static constructor
    /// surfaces as a FileNotFoundException too, and that type is unusable for every later call.</remarks>
    private static bool IsAboutOneFile(Exception ex)
    {
        var cur = ex;
        while (cur is TypeInitializationException or System.Reflection.TargetInvocationException or AggregateException
               && cur.InnerException is { } inner)
        {
            if (cur is TypeInitializationException) return false;
            cur = inner;
        }
        return cur is UnauthorizedAccessException or IOException;
    }

    /// <summary>
    /// True when the failure really is about what the model wrote. Judged on the <b>root</b>
    /// exception, for the same reason the message is.
    /// </summary>
    internal static bool IsAboutTheArguments(Exception ex)
    {
        var cur = ex;
        while (cur is TypeInitializationException or System.Reflection.TargetInvocationException
                   or AggregateException
               && cur.InnerException is { } inner)
            cur = inner;

        // ⚠ ArgumentException covers ArgumentNull/ArgumentOutOfRange, and it is what every path and
        // parameter guard in this product throws (PathSanitizer, the tools' own checks). A nested
        // ArgumentException keeps its own message on purpose — it is not a wrapper.
        return cur is ArgumentException or FormatException or System.Text.Json.JsonException;
    }
}
