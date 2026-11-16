using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #59: a hidden palette takes no part in selecting objects, and a selection over the limit of the settings
/// (15000 by default) is only counted — its objects are not read.
/// </summary>
public sealed class SelectionLimitTests
{
    [Fact]
    public void AnInactiveControllerNeitherReadsNorLetsTheSourceFollowTheSelection()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = Objects(3) };
        using var controller = new SelectionController(source, model.Selection, model.Settings, active: false);
        Assert.False(controller.Active);
        Assert.False(source.Tracking);
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, source.Counts);
        Assert.Empty(model.Selection.Entities);

        // The source is not tracking, but even an event or ⟳ of the palette reads nothing while it is hidden.
        source.Change();
        model.Selection.RequestRefresh();
        Assert.Equal(0, source.Reads);

        controller.Active = true; // XDATAPALETTE shows the palette.
        Assert.True(source.Tracking);
        Assert.Equal(1, source.Reads);
        Assert.Equal(3, model.Selection.Entities.Count);

        controller.Active = false; // The palette is closed by its title bar or by XDATAPALETTE.
        Assert.False(source.Tracking);
        Assert.Empty(model.Selection.Entities);
        Assert.False(model.Selection.HasSelection);
        source.Snapshot = Objects(5);
        source.Change();
        Assert.Equal(1, source.Reads);
        Assert.Empty(model.Selection.Entities);

        controller.Active = true; // Shown again: the current selection is read at once.
        Assert.Equal(2, source.Reads);
        Assert.Equal(5, model.Selection.Entities.Count);
    }

    [Fact]
    public void AnInactiveControllerDoesNotWrite()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = Objects(2) };
        using var controller = new SelectionController(source, model.Selection, model.Settings);
        Assert.True(source.Tracking);
        var edit = SelectionEdit.Header("Name", "X");
        Assert.True(model.Selection.RequestEdit(edit));
        Assert.Equal(1, source.Writes);
        controller.Active = false;
        Assert.False(model.Selection.RequestEdit(edit)); // Nothing is selected in a hidden palette.
        Assert.Equal(1, source.Writes);
    }

    [Fact]
    public void TheDefaultControllerStaysActiveAsBefore()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = Objects(1) };
        using var controller = new SelectionController(source, model.Selection);
        Assert.True(controller.Active);
        Assert.True(source.Tracking);
        Assert.Single(model.Selection.Entities);
    }

    [Fact]
    public void ASelectionOverTheLimitIsCountedButNotRead()
    {
        var model = new PaletteViewModel();
        model.Settings.MaxSelectionCount = 3;
        var source = new Source { Snapshot = Objects(4) };
        using var controller = new SelectionController(source, model.Selection, model.Settings);
        Assert.Equal(1, source.Counts);
        Assert.Equal(0, source.Reads); // The objects of the selection are not opened.
        Assert.Empty(model.Selection.Entities);
        Assert.Empty(model.Selection.Groups);
        Assert.False(model.Selection.HasSelection);
        Assert.Equal(4, model.Selection.SkippedCount);
        Assert.Equal(SelectionViewModel.OverLimitMessage(4, 3), model.Selection.Status);
        Assert.Equal(model.Selection.Status, model.Selection.Error);
        Assert.Contains("больше предела 3", model.Grid.Message);
        Assert.False(model.Selection.RequestEdit(SelectionEdit.Header("Name", "X")));
        Assert.Equal(0, source.Writes);

        source.Snapshot = Objects(3); // Exactly the limit is read.
        source.Change();
        Assert.Equal(1, source.Reads);
        Assert.Equal(3, model.Selection.Entities.Count);
        Assert.Equal(0, model.Selection.SkippedCount);
        Assert.Null(model.Selection.Error);
    }

    [Fact]
    public void ANewLimitAppliesToTheCurrentSelectionAtOnce()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = Objects(6) };
        using var controller = new SelectionController(source, model.Selection, model.Settings);
        Assert.Equal(6, model.Selection.Entities.Count);

        model.Settings.MaxSelectionCount = 5;
        Assert.Empty(model.Selection.Entities);
        Assert.Equal(6, model.Selection.SkippedCount);

        model.Settings.MaxSelectionCount = 0; // No limit.
        Assert.Equal(6, model.Selection.Entities.Count);

        model.Settings.ShowSystemGroup = true; // Other settings do not read the selection again.
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void ASourceWithoutACounterIsLimitedAfterTheRead()
    {
        var model = new PaletteViewModel();
        model.Settings.MaxSelectionCount = 2;
        var source = new UncountedSource { Snapshot = Objects(3) };
        using var controller = new SelectionController(source, model.Selection, model.Settings);
        Assert.Empty(model.Selection.Entities);
        Assert.Equal(3, model.Selection.SkippedCount);
    }

    [Fact]
    public void TheDefaultLimitIs15000()
    {
        var model = new PaletteViewModel();
        Assert.Equal(15000, PaletteSettings.DefaultMaxSelectionCount);
        Assert.Equal(15000, model.Settings.MaxSelectionCount);
        var source = new Source { CountOverride = 15001, Snapshot = Objects(1) };
        using var controller = new SelectionController(source, model.Selection, model.Settings);
        Assert.Equal(0, source.Reads);
        Assert.Equal(15001, model.Selection.SkippedCount);
        source.CountOverride = 15000;
        source.Change();
        Assert.Equal(1, source.Reads);
        Assert.Equal(0, model.Selection.SkippedCount);
    }

    [Fact]
    public void DisposalDetachesTheSettings()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = Objects(2) };
        var controller = new SelectionController(source, model.Selection, model.Settings);
        controller.Dispose();
        model.Settings.MaxSelectionCount = 1;
        Assert.Equal(1, source.Reads);
        Assert.Equal(2, model.Selection.Entities.Count);
        controller.Active = false; // Ignored after disposal.
        Assert.True(controller.Active);
    }

    [Fact]
    public void TheLimitIsKeptInTheSettingsFile()
    {
        var settings = new PaletteSettings();
        Assert.Contains("\"maxSelectionCount\": 15000", settings.ToJson());
        settings.MaxSelectionCount = 2500;
        var again = PaletteSettings.FromJson(settings.ToJson());
        Assert.Equal(2500, again.MaxSelectionCount);
        Assert.Equal(0, PaletteSettings.FromJson("{\"maxSelectionCount\": 0}").MaxSelectionCount);
        Assert.Equal(20000, PaletteSettings.FromJson("{\"maxSelectionCount\": 2e4}").MaxSelectionCount);

        var draft = again.Clone();
        Assert.Equal(2500, draft.MaxSelectionCount);
        draft.MaxSelectionCount = 7;
        Assert.Equal(2500, again.MaxSelectionCount); // Only «ОК» copies the draft back.
        again.CopyFrom(draft);
        Assert.Equal(7, again.MaxSelectionCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"maxSelectionCount\": -1}")]
    [InlineData("{\"maxSelectionCount\": 12.5}")]
    [InlineData("{\"maxSelectionCount\": 3000000000}")]
    [InlineData("{\"maxSelectionCount\": \"100\"}")]
    [InlineData("{\"maxSelectionCount\": null}")]
    [InlineData("не JSON")]
    public void AMissingOrWrongLimitGivesTheDefault(string json) =>
        Assert.Equal(PaletteSettings.DefaultMaxSelectionCount, PaletteSettings.FromJson(json).MaxSelectionCount);

    [Fact]
    public void ANegativeLimitIsRejected()
    {
        var settings = new PaletteSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxSelectionCount = -1);
        Assert.Equal(15000, settings.MaxSelectionCount);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, int.MaxValue, false)]
    [InlineData(10, 10, false)]
    [InlineData(10, 11, true)]
    public void TheLimitIsExceededOnlyAboveIt(int limit, int count, bool expected) =>
        Assert.Equal(expected, new PaletteSettings { MaxSelectionCount = limit }.IsOverSelectionLimit(count));

    [Theory]
    [InlineData("15000", 15000)]
    [InlineData(" 15 000 ", 15000)]
    [InlineData("15 000", 15000)]
    [InlineData("0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("", null)]
    [InlineData("-5", null)]
    [InlineData("+5", null)]
    [InlineData("1.5", null)]
    [InlineData("1,5", null)]
    [InlineData("abc", null)]
    [InlineData("2147483648", null)]
    public void TheSettingsWindowParsesTheLimit(string text, int? expected) =>
        Assert.Equal(expected, PaletteSettings.ParseMaxSelectionCount(text));

    private static EntityDataSnapshot[] Objects(int count) =>
        Enumerable.Range(1, count).Select(index => new EntityDataSnapshot(index.ToString("X"), "Line",
            new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1000, "Name=N" + index) }, Array.Empty<DataRecord>())).ToArray();

    private class UncountedSource : ISelectionSource, ISelectionWriter
    {
        private EventHandler? changed;
        public EntityDataSnapshot[] Snapshot { get; set; } = Array.Empty<EntityDataSnapshot>();
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public event EventHandler SelectionChanged { add => changed += value; remove => changed -= value; }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() { Reads++; return Snapshot; }
        public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit) => Writes++;
        public void Change() => changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Source : UncountedSource, ISelectionCounter, ISelectionTracker
    {
        public int Counts { get; private set; }
        public int? CountOverride { get; set; }
        public bool Tracking { get; set; }
        public int CountSelection() { Counts++; return CountOverride ?? Snapshot.Length; }
    }
}
