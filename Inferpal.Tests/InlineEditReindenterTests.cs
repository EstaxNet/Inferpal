using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure re-indentation applied to inline-edit ("Edit with AI") model output.
// The recurring real-world failure: local models drop the indentation of the lines they
// INSERT (flush to column 0) while keeping the surrounding lines correct, which the old
// single-uniform-shift could not repair. These tests pin the repaired behaviour.
public class InlineEditReindenterTests
{
    private static string R(string original, string edited) =>
        InlineEditReindenter.Reindent(original, edited).Replace("\r\n", "\n");

    [Fact]
    public void InsertedBlock_FlushLeft_IsRaisedToBraceDepth()
    {
        // Original selection: a method at 8-space base, body at 12.
        var original = string.Join('\n',
            "        private void ApplyState(ShellRunState state)",
            "        {",
            "            if (!state.StateCaptured) return;",
            "        }");

        // Model wrapped the body in a lock but flushed the inserted lines to column 0.
        var edited = string.Join('\n',
            "        private void ApplyState(ShellRunState state)",
            "        {",
            "            if (!state.StateCaptured) return;",
            "lock (_lock)",
            "{",
            "if (state.Cwd is not null) _cwd = state.Cwd;",
            "}",
            "        }");

        var expected = string.Join('\n',
            "        private void ApplyState(ShellRunState state)",
            "        {",
            "            if (!state.StateCaptured) return;",
            "            lock (_lock)",
            "            {",
            "                if (state.Cwd is not null) _cwd = state.Cwd;",
            "            }",
            "        }");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void FullyDedentedOutput_IsReanchoredToBase()
    {
        var original = string.Join('\n',
            "        void M()",
            "        {",
            "            Foo();",
            "        }");

        // Model returned everything at column 0 but kept relative nesting.
        var edited = string.Join('\n',
            "void M()",
            "{",
            "    Foo();",
            "    Bar();",
            "}");

        var expected = string.Join('\n',
            "        void M()",
            "        {",
            "            Foo();",
            "            Bar();",
            "        }");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void CorrectlyIndentedOutput_IsUnchanged()
    {
        var code = string.Join('\n',
            "        void M()",
            "        {",
            "            if (x)",
            "                Foo();",
            "        }");

        Assert.Equal(code, R(code, code));
    }

    [Fact]
    public void BraceLessBody_DeeperThanFloor_IsNotFlattened()
    {
        // The if-body has no braces; the model kept it correctly indented (depth+1 below the if).
        // A pure brace-depth reindent would wrongly flatten it onto the if's level.
        var original = string.Join('\n',
            "    void M()",
            "    {",
            "        Bar();",
            "    }");

        var edited = string.Join('\n',
            "void M()",
            "{",
            "    if (x)",
            "        Foo();",
            "    Bar();",
            "}");

        var expected = string.Join('\n',
            "    void M()",
            "    {",
            "        if (x)",
            "            Foo();",
            "        Bar();",
            "    }");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void BracesInStringsAndComments_DoNotAffectDepth()
    {
        var original = string.Join('\n',
            "    void M()",
            "    {",
            "    }");

        var edited = string.Join('\n',
            "void M()",
            "{",
            "    var s = \"}{)(\";   // a } brace ( in a comment",
            "    var t = $\"x{y}z\";",
            "    Foo();",
            "}");

        var expected = string.Join('\n',
            "    void M()",
            "    {",
            "        var s = \"}{)(\";   // a } brace ( in a comment",
            "        var t = $\"x{y}z\";",
            "        Foo();",
            "    }");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void MultiLineVerbatimString_ContentPreservedVerbatim()
    {
        var original = string.Join('\n',
            "    void M()",
            "    {",
            "    }");

        // The interior of the verbatim string (incl. its own indentation and braces)
        // must be left byte-for-byte, only the structural lines get reindented.
        var edited = string.Join('\n',
            "void M()",
            "{",
            "var s = @\"line1",
            "  indented } content {",
            "last\";",
            "Foo();",
            "}");

        var expected = string.Join('\n',
            "    void M()",
            "    {",
            "        var s = @\"line1",
            "  indented } content {",
            "last\";",
            "        Foo();",
            "    }");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void ClosingBraceLine_DedentsCorrectly()
    {
        var original = string.Join('\n',
            "    if (a)",
            "    {",
            "    }");

        var edited = string.Join('\n',
            "if (a)",
            "{",
            "DoThing();",
            "}");

        var expected = string.Join('\n',
            "    if (a)",
            "    {",
            "        DoThing();",
            "    }");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void TabIndentedOriginal_UsesTabs()
    {
        var original = string.Join('\n',
            "\tvoid M()",
            "\t{",
            "\t}");

        var edited = string.Join('\n',
            "void M()",
            "{",
            "Foo();",
            "}");

        var expected = string.Join('\n',
            "\tvoid M()",
            "\t{",
            "\t\tFoo();",
            "\t}");

        Assert.Equal(expected, R(original, edited));
    }

    [Fact]
    public void BlankLines_EmittedWithoutTrailingSpaces()
    {
        var original = string.Join('\n',
            "    void M()",
            "    {",
            "    }");

        var edited = string.Join('\n',
            "void M()",
            "{",
            "    Foo();",
            "",
            "    Bar();",
            "}");

        var result = R(original, edited);
        var lines  = result.Split('\n');
        Assert.Equal("", lines[3]); // the blank line carries no whitespace
    }

    /// <summary>
    /// ⚠ A selection that ends shallower than it starts keeps its levels: held at the first line, Python's <c>return 0</c>
    /// moved into the <c>if</c> above it — another program, no error — and a closing brace four columns right.
    /// </summary>
    [Fact]
    public void ASelectionThatEndsShallower_KeepsItsLowerLevels()
    {
        Assert.Equal("        return 2\n    return 0",
                     R("        return 1\n    return 0", "        return 2\n    return 0"));
        Assert.Equal("        x = 2;\n    }\n    y();",
                     R("        x = 1;\n    }\n    y();", "        x = 2;\n    }\n    y();"));
        // An echo comes back unchanged: the pipeline then sees "nothing to change" instead of applying an edit.
        Assert.Equal("        x = 1;\n    }", R("        x = 1;\n    }", "        x = 1;\n    }"));
        // Spaces in a file indented with tabs lose one level of them, never all.
        Assert.Equal("    b = 1", R("        a = 1\n    b = 1\n\tc", "        a = 2\n    b = 1").Split('\n')[1]);
        // A mixed indentation (a tab and two spaces) loses its spaces for the level below.
        Assert.Equal("\t  return 2\n\treturn 0", R("\t  return 1\n\treturn 0", "\t  return 2\n\treturn 0"));
        // Never below the original's own lowest level: a line the reply flushed to column 0 is raised to it.
        Assert.Equal("        return 2\n    return 0", R("        return 1\n    return 0", "        return 2\nreturn 0"));
    }

    [Fact]
    public void ASelectionThatNeverGoesBelowItsFirstLine_StillRaisesAFlushLeftLine()
    {
        // Reference arm: without a lower level in the original, a reply's shallower line is the model's slip, raised.
        Assert.Equal("    a = 1\n    b = 2", R("    a = 1\n    b = 1", "    a = 1\nb = 2"));
    }

    [Fact]
    public void EmptyOrWhitespaceEdited_ReturnedAsIs()
    {
        Assert.Equal("", R("    code", ""));
        Assert.Equal("   ", R("    code", "   "));
    }
}
