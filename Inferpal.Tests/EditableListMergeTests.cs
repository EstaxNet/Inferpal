using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The command and tool lists merged with what another editor wrote since this copy read them, entry by entry
/// (<c>CollectionShape.NameValueLines</c>): an entry is its name, however its line is written — merged line by line, a
/// line kept with spaces around its <c>=</c> and the settings window's line without read as two entries, and the name
/// ends up twice, the other editor's first.
/// </summary>
public sealed class EditableListMergeTests
{
    [Fact]
    public void AnEntryAnotherEditorAdded_IsKept_NextToTheOneChangedHere()
    {
        Assert.Equal(["#/a=A", "/b=B"],
                     EditableListText.Merge("/a=A", onDisk: "/a=A\n/b=B", mine: "#/a=A").Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnEntryWrittenWithSpaces_ChangedElsewhere_IsNotDoubled()
    {
        // The file holds it as written by hand; VS Code changed it; here, the window toggled another entry.
        var merged = EditableListText.Merge("/review = Relis\n/x=X", onDisk: "/review = Vérifie\n/x=X", mine: "/review=Relis\n#/x=X");

        Assert.Equal("/review = Vérifie\n#/x=X", merged);
    }

    [Fact]
    public void AnUntouchedEntry_KeepsHowTheFileWritesIt()
    {
        Assert.Equal("/review = Relis\n#/x=X",
                     EditableListText.Merge("/review = Relis\n/x=X", onDisk: "/review = Relis\n/x=X", mine: "/review=Relis\n#/x=X"));
    }

    [Fact]
    public void ANameChangedOnBothSides_KeepsThisCopysEntry_Once()
    {
        Assert.Equal("/r=C", EditableListText.Merge("/r=A", onDisk: "/R=B", mine: "/r=C"));
    }

    [Fact]
    public void ADisabledEntryBeforeAnActiveOneOfTheSameName_KeepsBoth()
    {
        // The disabled one does not run: the active one after it is the command, and neither may be dropped.
        const string mine = "#/review=Old\n/review=New\n#/x=X";
        Assert.Equal(mine, EditableListText.Merge("#/review=Old\n/review=New\n/x=X", onDisk: "#/review=Old\n/review=New\n/x=X", mine));
    }

    [Fact]
    public void AnEntryTurnedOffHere_WhileAnotherOfItsNameIsAddedElsewhere_StaysOff()
    {
        Assert.Equal("#/x=1", EditableListText.Merge("/x=1", onDisk: "#/x=0\n/x=1", mine: "#/x=1"));
    }

    [Fact]
    public void AnEntryRemovedHere_ChangedElsewhere_StaysRemoved()
    {
        Assert.Equal("/x=X", EditableListText.Merge("/a=A\n/x=X", onDisk: "/a=B\n/x=X", mine: "/x=X"));
    }

    [Fact]
    public void AnEntryRemovedElsewhere_AndLeftAsItWasHere_FollowsTheFile()
    {
        Assert.Equal("/a=A", EditableListText.Merge("/a=A\n/b=B", onDisk: "/a=A", mine: "/a=A\n/b=B"));
    }

    [Fact]
    public void ALineThatIsNoEntry_IsKept()
    {
        Assert.Equal("/a=A\nnot an entry", EditableListText.Merge("/a=A\nnot an entry", onDisk: "/a=A\nnot an entry", mine: "/a=A\nnot an entry"));
    }
}
