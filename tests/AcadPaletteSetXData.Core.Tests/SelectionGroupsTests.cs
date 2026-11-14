using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #41: the list above the panel groups the selection by <c>SYSTEMTYPE</c> or by the name AutoCAD gives the object
/// type; * keeps only the chosen group selected, − removes it from the selection. The localized names below stand for
/// what AutoCAD returns (the source reads them from AutoCAD, Core has no dictionary of names).
/// </summary>
public sealed class SelectionGroupsTests
{
    private const string LineName = "Отрезок";
    private const string PolylineName = "Полилиния";
    private const string BlockName = "Вхождение блока";

    [Fact]
    public void FiveLinesWithoutSystemTypeAreOneGroupWithTheAutoCadName()
    {
        Assert.Equal(new[] { "Отрезок (5)" }, Texts(Lines("L", 5)));
    }

    [Fact]
    public void FiveLinesWithSystemTypeAreOneGroupWithTheValue()
    {
        Assert.Equal(new[] { "Cable (5)" }, Texts(Lines("L", 5, "Cable")));
    }

    [Fact]
    public void GroupsByTheDisplayNameNotByTheClassOfTheObject()
    {
        var selection = Lines("L", 3, "Cable").Concat(Polylines("P", 2, "Cable")).ToArray();
        Assert.Equal(new[] { "Cable (5)" }, Texts(selection));
    }

    [Fact]
    public void SeveralGroupsAreListedAfterAllWithTheirCounts()
    {
        var selection = Lines("C", 3, "Cable").Concat(Lines("P", 2, "Pipe")).ToArray();
        Assert.Equal(new[] { "Все (5)", "Cable (3)", "Pipe (2)" }, Texts(selection));
    }

    [Fact]
    public void ObjectsWithoutSystemTypeUseTheirOwnAutoCadNames()
    {
        var selection = Lines("C", 3, "Cable")
            .Append(Snapshot("P1", "Polyline", PolylineName))
            .Append(Snapshot("B1", "BlockReference", BlockName)).ToArray();
        Assert.Equal(new[] { "Все (5)", "Cable (3)", "Полилиния (1)", "Вхождение блока (1)" }, Texts(selection));
    }

    [Fact]
    public void AllIsShownOnlyForMoreThanOneGroup()
    {
        // The example of the issue: 3 Cable, 2 Pipe and a line without SYSTEMTYPE.
        var selection = Lines("C", 3, "Cable").Concat(Lines("P", 2, "Pipe")).Concat(Lines("L", 1)).ToArray();
        var groups = SelectionGroups.Build(selection);
        Assert.Equal(new[] { "Все (6)", "Cable (3)", "Pipe (2)", "Отрезок (1)" }, groups.Select(group => group.Text));
        Assert.True(groups[0].IsAll);
        Assert.Equal(selection.Select(snapshot => snapshot.Handle), groups[0].Handles);
        Assert.False(SelectionGroups.Build(Lines("C", 2, "Cable").ToArray()).Single().IsAll);
        Assert.Empty(SelectionGroups.Build(Array.Empty<EntityDataSnapshot>()));
    }

    [Fact]
    public void LargerGroupsComeFirstAndEqualGroupsKeepTheOrderOfTheSelection()
    {
        var selection = Lines("P", 1, "Pipe").Concat(Lines("C", 2, "Cable")).Concat(Lines("L", 1)).ToArray();
        Assert.Equal(new[] { "Все (4)", "Cable (2)", "Pipe (1)", "Отрезок (1)" }, Texts(selection));
    }

    [Fact]
    public void AnEmptyOrBlankSystemTypeFallsBackToTheAutoCadName()
    {
        var selection = new[]
        {
            Snapshot("A1", "Line", LineName, V(1001, DrawingNames.RegApp), V(1000, "SYSTEMTYPE=")),
            Snapshot("A2", "Line", LineName, V(1001, DrawingNames.RegApp), V(1000, "SYSTEMTYPE =   ")),
            Snapshot("A3", "Line", LineName, V2(("SYSTEMTYPE", V(1000, " "))))
        };
        Assert.Equal(new[] { "Отрезок (3)" }, Texts(selection));
    }

    [Fact]
    public void WithoutAnAutoCadNameTheTypeOfTheSourceIsUsed()
    {
        var snapshot = new EntityDataSnapshot("A1", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>());
        Assert.Equal("Line", snapshot.TypeDisplayName);
        Assert.Equal("Line", SelectionGroups.DisplayName(snapshot));
        Assert.Equal("Line", new EntityDataSnapshot("A1", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>(), typeDisplayName: " ").TypeDisplayName);
        Assert.Equal(LineName, new EntityDataSnapshot("A1", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>(), typeDisplayName: " Отрезок ").TypeDisplayName);
    }

    [Fact]
    public void ReadsSystemTypeFromV2PairsV1StringsAndItsOwnRegApp()
    {
        Assert.Equal("Cable", SelectionGroups.SystemType(V2(("TEMPLATE", V(1000, "LINE")), ("SYSTEMTYPE", V(1000, " Cable ")))));
        Assert.Equal("Cable", SelectionGroups.SystemType(V2(("systemtype", V(1000, "Cable")))));
        Assert.Equal("3", SelectionGroups.SystemType(V2(("SYSTEMTYPE", V(1071, 3)))));
        Assert.Equal("1.5", SelectionGroups.SystemType(V2(("SYSTEMTYPE", V(1040, 1.5)))));
        Assert.Equal("Pipe", SelectionGroups.SystemType(new[] { V(1001, DrawingNames.RegApp), V(1000, "Name=Труба"), V(1000, "SYSTEMTYPE=Pipe") }));
        Assert.Equal("Pipe", SelectionGroups.SystemType(new[] { V(1001, "ESMT_LEP_v1.0"), V(1000, "SystemType = Pipe") }));
        Assert.Equal("Cable", SelectionGroups.SystemType(new[] { V(1001, "SYSTEMTYPE"), V(1000, "Cable") }));
        // The plugin RegApp is read first.
        Assert.Equal("Cable", SelectionGroups.SystemType(new[] { V(1001, "OTHER"), V(1000, "SYSTEMTYPE=Pipe"), V(1001, DrawingNames.RegApp), V(1000, "SYSTEMTYPE=Cable") }));
        Assert.Null(SelectionGroups.SystemType(Array.Empty<DataValue>()));
        Assert.Null(SelectionGroups.SystemType(new[] { V(1001, DrawingNames.RegApp), V(1000, "Name=SYSTEMTYPE") }));
    }

    [Fact]
    public void AV2ValueEqualToTheIdIsNotTakenForTheParameter()
    {
        // ("NAME" . "SYSTEMTYPE") ("POWER" . 2.5): "SYSTEMTYPE" is a value here, not an Id.
        Assert.Null(SelectionGroups.SystemType(V2(("NAME", V(1000, "SYSTEMTYPE")), ("POWER", V(1040, 2.5)))));
        // A list value is not a name.
        Assert.Null(SelectionGroups.SystemType(V2(("SYSTEMTYPE", V(1002, "{")), ("X", V(1000, "Cable")))));
    }

    [Fact]
    public void ListRefreshesWhenTheSelectionChangesAndKeepsTheChosenGroup()
    {
        var model = new PaletteViewModel();
        var source = new FakeSource { Snapshot = Lines("C", 3, "Cable").Concat(Lines("P", 2, "Pipe")).ToArray() };
        using var controller = new SelectionController(source, model.Selection);
        var selection = model.Selection;
        Assert.Equal(new[] { "Все (5)", "Cable (3)", "Pipe (2)" }, selection.Groups.Select(group => group.Text));
        Assert.True(selection.SelectedGroup!.IsAll);
        selection.SelectedGroup = selection.Groups[2];
        source.Snapshot = Lines("C", 1, "Cable").Concat(Lines("P", 4, "Pipe")).ToArray();
        source.Change();
        Assert.Equal(new[] { "Все (5)", "Pipe (4)", "Cable (1)" }, selection.Groups.Select(group => group.Text));
        Assert.Equal("Pipe (4)", selection.SelectedGroup!.Text);
        source.Snapshot = Array.Empty<EntityDataSnapshot>();
        source.Change();
        Assert.Empty(selection.Groups);
        Assert.Null(selection.SelectedGroup);
        Assert.False(selection.KeepGroupCommand.CanExecute(null));
        Assert.False(selection.ExcludeGroupCommand.CanExecute(null));
    }

    [Fact]
    public void KeepLeavesOnlyTheChosenGroupSelected()
    {
        var (model, source, controller) = Start();
        using (controller)
        {
            var selection = model.Selection;
            selection.SelectedGroup = selection.Groups.Single(group => group.Name == LineName);
            Assert.True(selection.KeepGroupCommand.CanExecute(null));
            selection.KeepGroupCommand.Execute(null);
            Assert.Equal(new[] { "L1", "L2", "L3", "L4", "L5" }, source.Selected);
            Assert.Equal(new[] { "Отрезок (5)" }, selection.Groups.Select(group => group.Text));
            Assert.Equal("Отрезок (5)", selection.SelectedGroup!.Text);
            Assert.Equal(5, selection.Entities.Count);
            Assert.Equal(1, source.Changes);
            Assert.Null(selection.Error);
        }
    }

    [Fact]
    public void ExcludeRemovesTheChosenGroupAndKeepsTheOthers()
    {
        var (model, source, controller) = Start();
        using (controller)
        {
            var selection = model.Selection;
            selection.SelectedGroup = selection.Groups.Single(group => group.Name == LineName);
            selection.ExcludeGroupCommand.Execute(null);
            Assert.Equal(new[] { "C1", "C2", "C3", "P1", "P2" }, source.Selected);
            Assert.Equal(new[] { "Все (5)", "Cable (3)", "Pipe (2)" }, selection.Groups.Select(group => group.Text));
            Assert.True(selection.SelectedGroup!.IsAll);
            Assert.Equal(1, source.Changes);
        }
    }

    [Fact]
    public void KeepOfAllChangesNothingAndExcludeOfAllClearsTheSelection()
    {
        var (model, source, controller) = Start();
        using (controller)
        {
            var selection = model.Selection;
            Assert.True(selection.SelectedGroup!.IsAll);
            Assert.Equal("Все (10)", selection.SelectedGroup.Text);
            Assert.True(selection.RequestKeep(selection.SelectedGroup));
            Assert.Equal(0, source.Changes);
            Assert.Equal(10, selection.Entities.Count);
            selection.ExcludeGroupCommand.Execute(null);
            Assert.Equal(1, source.Changes);
            Assert.Empty(source.Selected);
            Assert.Empty(selection.Groups);
            Assert.False(selection.HasSelection);
        }
    }

    [Fact]
    public void ButtonsNeverWriteXData()
    {
        var (model, source, controller) = Start();
        using (controller)
        {
            var before = source.Snapshot.Select(snapshot => Dump(snapshot.XData)).ToArray();
            var selection = model.Selection;
            selection.SelectedGroup = selection.Groups.Single(group => group.Name == "Cable");
            selection.ExcludeGroupCommand.Execute(null);
            selection.SelectedGroup = selection.Groups.Single(group => group.Name == "Pipe");
            selection.KeepGroupCommand.Execute(null);
            Assert.Equal(new[] { "P1", "P2" }, source.Selected);
            Assert.Equal(0, source.Writes);
            Assert.Equal(before, source.Snapshot.Select(snapshot => Dump(snapshot.XData)).ToArray());
        }
    }

    [Fact]
    public void ButtonsAreDisabledWhenTheSourceCannotChangeTheSelection()
    {
        var model = new PaletteViewModel();
        var source = new FakeSource { Snapshot = Lines("C", 2, "Cable").Concat(Lines("L", 1)).ToArray() };
        using var controller = new SelectionController(source, model.Selection);
        var selection = model.Selection;
        Assert.False(selection.CanChangeSelection);
        Assert.False(selection.KeepGroupCommand.CanExecute(null));
        Assert.False(selection.RequestExclude(selection.SelectedGroup));
        Assert.Equal(3, selection.Entities.Count);
    }

    [Fact]
    public void AFailedChangeIsReportedAndTheListIsReadAgain()
    {
        var (model, source, controller) = Start();
        using (controller)
        {
            source.ChangeError = new InvalidOperationException("команда активна");
            var selection = model.Selection;
            selection.SelectedGroup = selection.Groups.Single(group => group.Name == "Pipe");
            Assert.False(selection.RequestKeep(selection.SelectedGroup));
            Assert.Contains("команда активна", selection.Error);
            Assert.Equal("Все (10)", selection.Groups[0].Text);
            Assert.Equal("Pipe (2)", selection.SelectedGroup!.Text);
        }
    }

    [Fact]
    public void DisposalDisablesTheButtons()
    {
        var (model, _, controller) = Start();
        Assert.True(model.Selection.CanChangeSelection);
        controller.Dispose();
        Assert.False(model.Selection.CanChangeSelection);
        Assert.False(model.Selection.ExcludeGroupCommand.CanExecute(null));
    }

    /// <summary>The example of the issue: 5 lines, 3 Cable and 2 Pipe.</summary>
    private static (PaletteViewModel Model, ChangingSource Source, SelectionController Controller) Start()
    {
        var model = new PaletteViewModel();
        var source = new ChangingSource(Lines("L", 5).Concat(Lines("C", 3, "Cable")).Concat(Polylines("P", 2, "Pipe")).ToArray());
        var controller = new SelectionController(source, model.Selection);
        Assert.Equal(new[] { "Все (10)", "Отрезок (5)", "Cable (3)", "Pipe (2)" }, model.Selection.Groups.Select(group => group.Text));
        return (model, source, controller);
    }

    private static string[] Texts(IEnumerable<EntityDataSnapshot> selection) => SelectionGroups.Build(selection.ToArray()).Select(group => group.Text).ToArray();

    private static IEnumerable<EntityDataSnapshot> Lines(string prefix, int count, string? systemType = null) =>
        Enumerable.Range(1, count).Select(index => Snapshot(prefix + index, "Line", LineName, systemType is null ? Array.Empty<DataValue>() : V2(("SYSTEMTYPE", V(1000, systemType)))));

    private static IEnumerable<EntityDataSnapshot> Polylines(string prefix, int count, string? systemType = null) =>
        Enumerable.Range(1, count).Select(index => Snapshot(prefix + index, "Polyline", PolylineName, systemType is null ? Array.Empty<DataValue>() : V2(("SYSTEMTYPE", V(1000, systemType)))));

    private static EntityDataSnapshot Snapshot(string handle, string type, string name, params DataValue[] xdata) =>
        new EntityDataSnapshot(handle, type, xdata, Array.Empty<DataRecord>(), typeDisplayName: name);

    private static DataValue[] V2(params (string Id, DataValue Value)[] pairs)
    {
        var values = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)XDataV2.FormatVersion) };
        foreach (var pair in pairs) { values.Add(V(1000, pair.Id)); values.Add(pair.Value); }
        return values.ToArray();
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);

    private static string Dump(IReadOnlyList<DataValue> values) => string.Join("|", values.Select(value => value.TypeCode + ":" + value.Value));

    /// <summary>A drawing with PICKFIRST: * and − select a part of it, writes are counted to prove there are none.</summary>
    private sealed class ChangingSource : ISelectionSource, ISelectionChanger, ISelectionWriter
    {
        private readonly EntityDataSnapshot[] drawing;

        public ChangingSource(EntityDataSnapshot[] drawing) { this.drawing = drawing; Selected = drawing.Select(item => item.Handle).ToArray(); }
        public EntityDataSnapshot[] Snapshot => drawing;
        public IReadOnlyList<string> Selected { get; private set; }
        public int Changes { get; private set; }
        public int Writes { get; private set; }
        public Exception? ChangeError { get; set; }
        public event EventHandler SelectionChanged { add { } remove { } }

        public IReadOnlyList<EntityDataSnapshot> ReadSelection() => drawing.Where(item => Selected.Contains(item.Handle)).ToArray();

        public void SetSelection(IReadOnlyList<string> handles)
        {
            if (ChangeError != null) throw ChangeError;
            Assert.All(handles, handle => Assert.Contains(handle, Selected));
            Changes++;
            Selected = handles.ToArray();
        }

        public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit) => Writes++;
    }

    private sealed class FakeSource : ISelectionSource
    {
        private EventHandler? changed;
        public EntityDataSnapshot[] Snapshot { get; set; } = Array.Empty<EntityDataSnapshot>();
        public event EventHandler SelectionChanged { add => changed += value; remove => changed -= value; }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() => Snapshot;
        public void Change() => changed?.Invoke(this, EventArgs.Empty);
    }
}
