using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #57: a <c>CalculatedString</c> could be added to an object only with a template. «+ Параметр» now offers it as
/// well; its row marks the object (the computed text for <c>Persist</c>, an empty string otherwise) and the tree shows
/// the computed result. The schema is examples/issue43-formula-schema.json.
/// </summary>
public sealed class AddCalculatedStringTests
{
    [Fact]
    public void PlusParameterOffersAndAddsACalculatedStringWithoutATemplate()
    {
        var drawing = new Drawing(FormulaCache(),
            Panel("A", Pair("Number", "1"), Pair("Group", "2"), Pair("Name", "Щит"), Pair("Power", 2.5), Pair("Reserve", 0.5), Pair("Count", 2)),
            Panel("B", Pair("Number", "1"), Pair("Group", "3"), Pair("Name", "Щит"), Pair("Power", 1.0), Pair("Reserve", 0.0), Pair("Count", 1)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.Contains(grid.AvailableParameters, option => option.Key == "Total");
            Assert.Contains(grid.AvailableParameters, option => option.Key == "Label");
            Assert.Equal("Расчётная мощность (Total)", grid.AvailableParameters.Single(option => option.Key == "Total").Caption);
            Assert.Throws<KeyNotFoundException>(() => Row(grid, "Total"));

            // Persist: the computed result is written at once.
            Assert.True(grid.AddParameter("Total"));
            Assert.Equal(new object[] { "6", "1" }, drawing.Stored("Total"));
            var total = Row(grid, "Total");
            Assert.Same(total, grid.SelectedRow);
            Assert.Equal(ObjectCalculation.Various, total.Value);
            Assert.False(total.IsEditable);
            Assert.Equal(PropertyState.Calculated, total.State);
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Total");

            // Without persist only the row is written; the tree shows the computed text.
            Assert.True(grid.AddParameter("Label"));
            Assert.Equal(new object[] { "", "" }, drawing.Stored("Label"));
            var label = Row(grid, "Label");
            Assert.Equal(ObjectCalculation.Various, label.Value);
            Assert.Equal(PropertyState.Calculated, label.State);
            Assert.False(label.IsEditable);

            // A stored Persist result follows its sources (TZXDATA 7.4).
            var power = Row(grid, "Power");
            power.EditText = "4";
            Assert.True(grid.Commit(power));
            Assert.Equal(new object[] { "9", "4" }, drawing.Stored("Total"));
            Assert.Equal(new object[] { "", "" }, drawing.Stored("Label"));

            // A formula is read only: × removes it from every object, its sources stay.
            Assert.True(Row(grid, "Label").CanDelete);
            Assert.False(Row(grid, "Power").CanDelete); // Without a template a value is cleared by an empty input.
            Assert.True(grid.DeleteRow(Row(grid, "Label")));
            Assert.Equal(new object?[] { null, null }, drawing.Stored("Label"));
            Assert.Throws<KeyNotFoundException>(() => Row(grid, "Label"));
            Assert.Contains(grid.AvailableParameters, option => option.Key == "Label");
            Assert.Equal(new object[] { "9", "4" }, drawing.Stored("Total"));
        }
    }

    [Fact]
    public void AFormulaOfTheTemplateHasNoCrossAndAnotherOneGoesToTheExtraGroup()
    {
        // examples/schema.json: GC_Name belongs to DEVICE, but not to TRAY.
        var drawing = new Drawing(
            Device("A", Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "7")),
            Object("B", "TRAY", Pair("Tray_Width", 100)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.False(Row(grid, "GC_Name").CanDelete);
        }

        var tray = new Drawing(Object("B", "TRAY", Pair("Tray_Width", 100)));
        var (_, trayGrid, trayController) = Open(tray);
        using (trayController)
        {
            Assert.True(trayGrid.AddParameter("GC_Name"));
            Assert.Equal(new object[] { "" }, tray.Stored("GC_Name"));
            var name = Row(trayGrid, "GC_Name");
            Assert.True(name.CanDelete);
            Assert.Contains(name, trayGrid.Find("g:" + PropertyGridViewModel.GroupExtra)!.Children);
        }
    }

    [Fact]
    public void OneObjectShowsTheComputedResultsOfTheAddedFormulas()
    {
        var drawing = new Drawing(FormulaCache(),
            Panel("A", Pair("Number", "1"), Pair("Group", "2"), Pair("Name", "Щит"), Pair("Power", 2.5), Pair("Reserve", 0.5), Pair("Count", 2)));
        var (selection, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.True(grid.AddParameter("Label"));
            Assert.True(grid.AddParameter("TotalText"));
            Assert.Equal("ЩУ1.2 — Щит", Row(grid, "Label").Value);
            Assert.Equal("P=6 кВт", Row(grid, "TotalText").Value);
            Assert.Equal(new object?[] { null }, drawing.Stored("Total"));
            // The empty row of a formula without persist is not an error of the object.
            Assert.DoesNotContain("Label", selection.Diagnostics);
            Assert.DoesNotContain("TotalText", selection.Diagnostics);
        }
    }

    [Fact]
    public void AddCalculatedWritesTheMarkerAndKeepsAStoredRow()
    {
        var cache = FormulaCache();
        var patch = new XDataPatch(cache.Schema, culture: CultureInfo.InvariantCulture);
        var panel = Panel("A", Pair("Power", 2.5), Pair("Reserve", 0.5), Pair("Count", 2));

        var persisted = XDataV2.Read(patch.Apply(panel, SelectionEdit.AddCalculated("total")), cache.Schema);
        Assert.Equal("6", persisted.Find("Total")!.Value);
        Assert.Equal("", XDataV2.Read(patch.Apply(panel, SelectionEdit.AddCalculated("Label")), cache.Schema).Find("Label")!.Value);

        // A stored row is kept: a stale Persist is updated by its sources or VELEC_CHECK, not by «+ Параметр».
        var stored = Panel("A", Pair("Power", 2.5), Pair("Total", "старое"));
        Assert.Equal("старое", XDataV2.Read(patch.Apply(stored, SelectionEdit.AddCalculated("Total")), cache.Schema).Find("Total")!.Value);

        // Other parameters keep SetParameter; a formula is still not set by hand.
        Assert.Throws<InvalidOperationException>(() => patch.Apply(panel, SelectionEdit.AddCalculated("Power")));
        Assert.Throws<InvalidOperationException>(() => patch.Apply(panel, SelectionEdit.SetParameter("Total", "1")));
        Assert.Throws<ArgumentException>(() => SelectionEdit.AddCalculated(""));
    }

    [Fact]
    public void AnObjectWithoutXDataGetsTheSectionAndTheRow()
    {
        var cache = FormulaCache();
        var patch = new XDataPatch(cache.Schema, culture: CultureInfo.InvariantCulture);
        var empty = new EntityDataSnapshot("A", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>());
        var values = XDataV2.Read(patch.Apply(empty, SelectionEdit.AddCalculated("Total")), cache.Schema);
        Assert.Equal(XDataFormat.V2, values.Format);
        Assert.Equal("0", values.Find("Total")!.Value); // (0 + 0) * 1 with the defaults of the schema.
        var powered = XDataV2.Read(patch.Apply(patch.Apply(empty, SelectionEdit.AddCalculated("Total")), SelectionEdit.SetParameter("Power", 2.5)), cache.Schema);
        Assert.Equal("2.5", powered.Find("Total")!.Value);
    }

    private static ObjectValuesCache FormulaCache()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "issue43-formula-schema.json")));
        Assert.NotNull(result.Schema);
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }

    /// <summary>An object with XDATA v2 and no template: formulas can be added only by «+ Параметр».</summary>
    private static EntityDataSnapshot Panel(string handle, params DataValue[][] pairs)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2) };
        foreach (var pair in pairs) xdata.AddRange(pair);
        return new EntityDataSnapshot(handle, "BlockReference", xdata.ToArray(), Array.Empty<DataRecord>());
    }
}
