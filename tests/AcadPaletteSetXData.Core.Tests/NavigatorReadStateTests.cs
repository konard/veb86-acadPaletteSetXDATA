using System.Linq;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #66: the bookkeeping of the AutoCAD source of the navigator. The first read reads every object of the space;
/// later reads read only the objects changed since; Undo/Redo, too many nested changes and a failed read give a full read.
/// </summary>
public sealed class NavigatorReadStateTests
{
    private static NavigatorReadState<int, string> Loaded(params int[] ids)
    {
        var state = new NavigatorReadState<int, string>("drawing.dwg#1", nestedLimit: 3);
        Assert.Null(state.BeginRead(false));
        foreach (var id in ids) state.Store(id, "v" + id);
        state.EndRead();
        return state;
    }

    [Fact]
    public void TheFirstReadReadsEverythingAndChangesBeforeItAreIgnored()
    {
        var state = new NavigatorReadState<int, string>("drawing.dwg#1");
        Assert.True(state.NeedsFullRead);
        Assert.False(state.MarkObject(1));
        Assert.False(state.MarkAll());
        Assert.False(state.Pending);
        Assert.Null(state.BeginRead(false));
        state.Store(1, "a");
        state.Store(2, "b");
        state.EndRead();
        Assert.False(state.NeedsFullRead);
        Assert.Equal(new[] { "a", "b" }, state.Items);
        Assert.Equal("drawing.dwg#1", state.Key);
    }

    [Fact]
    public void APartialReadReadsOnlyTheChangedObjects()
    {
        var state = Loaded(1, 2, 3);
        Assert.False(state.Pending);
        var nothing = state.BeginRead(false);
        Assert.NotNull(nothing);
        Assert.Empty(nothing!.Objects);
        state.EndRead();

        Assert.True(state.MarkObject(2));
        Assert.True(state.MarkObject(2));
        Assert.True(state.MarkObject(4));
        Assert.True(state.MarkNested(40));
        Assert.True(state.Pending);
        var plan = state.BeginRead(false);
        Assert.NotNull(plan);
        Assert.Equal(new[] { 2, 4 }, plan!.Objects.OrderBy(id => id));
        Assert.Equal(new[] { 40 }, plan.Nested);
        Assert.False(state.Pending);
        state.Store(2, "v2 изменён");
        state.Store(4, "v4");
        state.EndRead();
        Assert.Equal(new[] { "v1", "v2 изменён", "v3", "v4" }, state.Items);
    }

    [Fact]
    public void ErasedAndFailedObjectsLeaveTheListAndFailuresAreCountedUntilReadAgain()
    {
        var state = Loaded(1, 2, 3);
        state.MarkObject(1);
        state.MarkObject(2);
        state.BeginRead(false);
        state.Drop(1);
        state.Fail(2);
        state.EndRead();
        Assert.Equal(new[] { "v3" }, state.Items);
        Assert.Equal(1, state.FailedCount);
        Assert.Equal(1, state.Count);

        state.MarkObject(2);
        state.BeginRead(false);
        state.Store(2, "v2");
        state.EndRead();
        Assert.Equal(0, state.FailedCount);
        Assert.Equal(new[] { "v3", "v2" }, state.Items);
    }

    [Fact]
    public void UndoTooManyNestedChangesAndAFullRefreshReadEverythingAgain()
    {
        var state = Loaded(1, 2);
        Assert.True(state.MarkAll());
        Assert.True(state.NeedsFullRead);
        Assert.Null(state.BeginRead(false));
        Assert.Empty(state.Items);
        state.Store(1, "v1");
        state.EndRead();

        for (var id = 10; id < 14; id++) state.MarkNested(id);
        Assert.True(state.NeedsFullRead);
        Assert.Null(state.BeginRead(false));
        state.EndRead();

        state.MarkObject(1);
        Assert.Null(state.BeginRead(full: true));
        state.EndRead();
        Assert.False(state.NeedsFullRead);
    }

    [Fact]
    public void ASchemaChangeOnlyAsksForARefresh()
    {
        var state = Loaded(1);
        Assert.True(state.MarkValues());
        Assert.True(state.Pending);
        Assert.False(state.NeedsFullRead);
        var plan = state.BeginRead(false);
        Assert.Empty(plan!.Objects);
        state.EndRead();
        Assert.Equal(new[] { "v1" }, state.Items);
    }

    [Fact]
    public void AReadThatFailedMakesTheNextReadFull()
    {
        var state = Loaded(1, 2);
        state.MarkObject(1);
        Assert.NotNull(state.BeginRead(false));
        // AutoCAD failed before EndRead: changes are not recorded, the next read reads everything.
        Assert.False(state.MarkObject(2));
        Assert.True(state.NeedsFullRead);
        Assert.Null(state.BeginRead(false));
    }
}
