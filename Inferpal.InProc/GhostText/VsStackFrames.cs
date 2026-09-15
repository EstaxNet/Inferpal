using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Inferpal.GhostText;

/// <summary>
/// Which stack frame the IDE currently has selected — the frame <c>Debugger.CurrentStackFrame</c>
/// reads its locals from.
/// </summary>
/// <remarks>
/// <para>
/// Under Visual Studio the locals do <b>not</b> belong to the top of the stack. Just My Code moves
/// the selection off runtime frames, the user can click any frame, and <c>debug_inspect</c> moves
/// it itself when it scopes an evaluation (<c>VsDebugDriver.Evaluate</c>) — so the frame is not
/// even stable between two tool calls. The reader has to be told which frame it got, and that
/// starts with knowing.
/// </para>
/// <para>
/// ⚠ EnvDTE hands out a fresh runtime callable wrapper per call, so <c>ReferenceEquals</c> answers
/// false for one and the same frame. COM identity is the <c>IUnknown</c> pointer, which is what
/// this compares. When it cannot tell, it says so with <c>null</c> — callers then claim nothing,
/// which is the whole point of the exercise.
/// </para>
/// </remarks>
internal static class VsStackFrames
{
    /// <summary>
    /// Zero-based position of <paramref name="current"/> in <paramref name="frames"/> (the stack in
    /// enumeration order, top first), or <c>null</c> when it is absent, unknown, or beyond the
    /// captured window.
    /// </summary>
    internal static int? PositionOf(IReadOnlyList<object> frames, object? current)
    {
        if (current is null) return null;

        for (var i = 0; i < frames.Count; i++)
            if (IsSameComObject(frames[i], current))
                return i;

        return null;
    }

    /// <summary>True when the two references wrap the same COM object.</summary>
    internal static bool IsSameComObject(object? a, object? b)
    {
        if (a is null || b is null) return false;
        if (ReferenceEquals(a, b)) return true;

        var pa = IntPtr.Zero;
        var pb = IntPtr.Zero;
        try
        {
            pa = Marshal.GetIUnknownForObject(a);
            pb = Marshal.GetIUnknownForObject(b);
            return pa == pb;
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("VsStackFrames.Identity", ex);
            return false;
        }
        finally
        {
            if (pa != IntPtr.Zero) Marshal.Release(pa);
            if (pb != IntPtr.Zero) Marshal.Release(pb);
        }
    }
}
