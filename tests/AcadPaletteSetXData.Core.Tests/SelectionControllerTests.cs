using System;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class SelectionControllerTests
{
    [Fact]
    public void EmptySelectionDisablesTheEditorAndMutationCommands()
    {
        var model = new PaletteViewModel();
        Assert.False(model.Selection.HasSelection);
        Assert.False(model.Properties.IsEnabled);
        Assert.True(model.Properties.IsReadOnly);
        Assert.False(model.Properties.AddMaterialCommand.CanExecute(null));
        model.Properties.AddMaterialCommand.Execute(null);
        Assert.Empty(model.Properties.Materials);
    }

    [Fact]
    public void ReadsInitialSelectionAndChangesAndClearsStaleValuesOnDeselection()
    {
        var model = new PaletteViewModel();
        var source = new FakeSelectionSource { Snapshot = new[] { Entity("A1", "One") } };
        using var controller = new SelectionController(source, model.Selection);
        Assert.True(model.Selection.HasSelection);
        Assert.Equal("One", model.Properties.Name);
        Assert.False(model.Properties.CanEdit);
        source.Snapshot = new[] { Entity("B2", "Two") };
        source.Change();
        Assert.Equal("Two", model.Properties.Name);
        source.Snapshot = Array.Empty<EntityDataSnapshot>();
        source.Change();
        Assert.False(model.Selection.HasSelection);
        Assert.Equal("", model.Properties.Name);
        Assert.Empty(model.Selection.Tree);
        Assert.Empty(model.Properties.Materials);
    }

    [Fact]
    public void KeepsIndividualRawTreesWhilePropertiesAlwaysRepresentTheGroup()
    {
        var model = new PaletteViewModel();
        var source = new FakeSelectionSource { Snapshot = new[] { Entity("A1", "One"), Entity("B2", "Two") } };
        using var controller = new SelectionController(source, model.Selection);
        Assert.Equal(2, model.Selection.Entities.Count);
        Assert.True(model.Properties.IsNameMixed);
        model.Selection.SelectedEntity = model.Selection.Entities[1];
        Assert.True(model.Properties.IsNameMixed);
        Assert.Equal("B2", model.Selection.SelectedEntity!.Handle);
        source.Change();
        Assert.Equal("B2", model.Selection.SelectedEntity!.Handle);
        model.Theme = PaletteTheme.Light;
        model.SelectedTabIndex = 1;
        Assert.True(model.Properties.IsNameMixed);
    }

    [Fact]
    public void MissingDataIsAnEnabledEmptyReadOnlyViewAndReadErrorsClearOldData()
    {
        var model = new PaletteViewModel();
        var source = new FakeSelectionSource { Snapshot = new[]
        {
            new EntityDataSnapshot("A1", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>())
        } };
        using var controller = new SelectionController(source, model.Selection);
        Assert.True(model.Properties.IsEnabled);
        Assert.Empty(model.Properties.Materials);
        source.Error = new InvalidOperationException("read failed");
        source.Change();
        Assert.False(model.Selection.HasSelection);
        Assert.Contains("read failed", model.Selection.Status);
    }

    [Fact]
    public void DisposalDetachesSelectionSourceAndIsIdempotent()
    {
        var source = new FakeSelectionSource();
        var model = new PaletteViewModel();
        var controller = new SelectionController(source, model.Selection);
        Assert.Equal(1, source.Subscribers);
        controller.Dispose();
        controller.Dispose();
        Assert.Equal(0, source.Subscribers);
        source.Snapshot = new[] { Entity("A1", "ignored") };
        source.Change();
        Assert.Empty(model.Selection.Entities);
    }

    private static EntityDataSnapshot Entity(string handle, string name) =>
        new EntityDataSnapshot(handle, "Line", new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1000, "Name=" + name) },
            Array.Empty<DataRecord>());

    private sealed class FakeSelectionSource : ISelectionSource
    {
        private EventHandler? changed;
        public int Subscribers { get; private set; }
        public EntityDataSnapshot[] Snapshot { get; set; } = Array.Empty<EntityDataSnapshot>();
        public Exception? Error { get; set; }
        public event EventHandler SelectionChanged
        {
            add { changed += value; Subscribers++; }
            remove { changed -= value; Subscribers--; }
        }
        public System.Collections.Generic.IReadOnlyList<EntityDataSnapshot> ReadSelection() =>
            Error == null ? Snapshot : throw Error;
        public void Change() => changed?.Invoke(this, EventArgs.Empty);
    }
}
