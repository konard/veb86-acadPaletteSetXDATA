using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// TZXDATA stage 7: «Шаблон ▾» / <c>VELEC_TEMPLATE_APPLY</c>, <c>DEVICE_ID</c>, the change of a template (6.6), template
/// defaults and «Зафиксировать значения по умолчанию» (6.2). Acceptance: the template of a group is assigned in one
/// transaction; values outside the template are kept.
/// </summary>
public sealed class ObjectTemplatesTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [Fact]
    public void AssigningATemplateToAGroupIsOneTransactionAndCreatesDeviceIds()
    {
        var other = new[] { V(1001, "OTHER_APP"), V(1000, "kept") };
        var drawing = new Drawing(Empty("A"), Empty("B", other), Empty("C"));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.True(grid.NeedsTemplate);
            Assert.True(grid.CanEdit);
            Assert.Equal("нет", grid.TemplateText);
            Assert.False(grid.HasTemplate);
            Assert.False(grid.HasDefaults);
            // EntityTypes: only the templates of BlockReference are offered.
            Assert.Equal(new[] { "DEVICE", "MOTOR" }, grid.AvailableTemplates.Select(option => option.Key));
            Assert.Equal("Устройство (DEVICE)", grid.AvailableTemplates[0].Caption);

            Assert.True(grid.AssignTemplate("MOTOR"));
            Assert.Equal(1, drawing.Commits); // One transaction, one Undo step.
            Assert.Equal(3, drawing.Writes);
            var values = drawing.Snapshots.Select(drawing.Read).ToArray();
            Assert.All(values, item => Assert.Equal("MOTOR", item.Template));
            Assert.All(values, item => Assert.Equal(XDataFormat.V2, item.Format));
            Assert.All(values, item => Assert.True(Guid.TryParse(item.DeviceId, out _), item.DeviceId));
            Assert.Equal(3, values.Select(item => item.DeviceId).Distinct().Count()); // Every object gets its own GUID.
            // DEVICE_ID and TEMPLATE are the first pairs (TZXDATA 6.1); other RegApps are kept.
            var b = drawing.Snapshots[1].XData;
            Assert.Equal(new object[] { "OTHER_APP", "kept", DrawingNames.RegApp, (short)2, "DEVICE_ID" }, b.Take(5).Select(value => value.Value));
            Assert.Equal(new object[] { "TEMPLATE", "MOTOR" }, b.Skip(6).Take(2).Select(value => value.Value));
            Assert.Equal(8, b.Count); // Defaults are not written (TZXDATA 6.2).

            Assert.False(grid.NeedsTemplate);
            Assert.True(grid.HasTemplate);
            Assert.Equal("Электродвигатель", grid.TemplateText);
            // Template defaults are shown with ○ and «по умолчанию»; the template default overrides the parameter one.
            var poles = Row(grid, "Poles");
            Assert.Equal(PropertyState.Default, poles.State);
            Assert.Equal("○", poles.StateText);
            Assert.Equal("3", poles.Value);
            Assert.Equal(PropertyGridViewModel.DefaultNote, poles.Note);
            Assert.Equal("Электродвигатель", Row(grid, "Device_Type").Value);
            Assert.Equal("380 В", Row(grid, "Voltage").Value);
            Assert.True(grid.HasDefaults);
            Assert.True(grid.CommitDefaultsCommand.CanExecute(null));

            // The same template again writes nothing.
            Assert.False(grid.AssignTemplate("MOTOR"));
            Assert.Equal(1, drawing.Commits);
        }
    }

    [Fact]
    public void ChangingTheTemplateKeepsDeviceIdAndValuesOutsideTheNewTemplate()
    {
        var id = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";
        var drawing = new Drawing(
            WithId("A", id, "DEVICE", Pair("Device_Class", "Щит"), Pair("Power", 2.5), Pair("Legacy", "x")),
            WithId("B", "6F9619FF-8B86-D011-B42D-00C04FC964FF", "DEVICE", Pair("Device_Class", "Щит"), Pair("Power", 2.5), Pair("Legacy", "x")));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.Equal("Устройство", grid.TemplateText);
            Assert.Null(grid.Find("g:" + PropertyGridViewModel.GroupExtra));

            grid.AssignTemplateCommand.Execute(grid.AvailableTemplates.Single(option => option.Key == "MOTOR"));
            Assert.Equal(1, drawing.Commits);
            Assert.Equal(new object?[] { "MOTOR", "MOTOR" }, drawing.Snapshots.Select(snapshot => drawing.Read(snapshot).Template));
            Assert.Equal(id, drawing.Read(drawing.Snapshots[0]).DeviceId); // DEVICE_ID is kept.
            // Every value is kept (TZXDATA 6.6); Device_Class is not in MOTOR and goes to «Дополнительные» with ×.
            Assert.Equal(new object[] { "Щит", "Щит" }, drawing.Stored("Device_Class"));
            Assert.Equal(new object[] { 2.5, 2.5 }, drawing.Stored("Power"));
            Assert.Equal(new object[] { "x", "x" }, drawing.Stored("Legacy"));
            var extra = grid.Find("g:" + PropertyGridViewModel.GroupExtra);
            Assert.NotNull(extra);
            var deviceClass = Assert.Single(extra!.Children);
            Assert.Equal("Device_Class", deviceClass.Id);
            Assert.True(deviceClass.CanDelete);
            Assert.Equal(PropertyState.Set, Row(grid, "Power").State);
            Assert.Equal(PropertyState.Default, Row(grid, "Poles").State);

            // Back to DEVICE: the value returns to its template group.
            Assert.True(grid.AssignTemplate("DEVICE"));
            Assert.Null(grid.Find("g:" + PropertyGridViewModel.GroupExtra));
            Assert.Equal(PropertyState.Set, Row(grid, "Device_Class").State);

            // «Без шаблона»: TEMPLATE is removed, values and DEVICE_ID stay.
            Assert.True(grid.AssignTemplate(""));
            Assert.Equal(3, drawing.Commits);
            Assert.All(drawing.Snapshots.Select(drawing.Read), item => Assert.Null(item.Template));
            Assert.Equal(id, drawing.Read(drawing.Snapshots[0]).DeviceId);
            Assert.Equal(new object[] { "Щит", "Щит" }, drawing.Stored("Device_Class"));
            Assert.Equal("нет", grid.TemplateText);
            Assert.False(grid.HasTemplate);
            Assert.False(grid.AssignTemplate("")); // Nothing to remove.
            Assert.Throws<KeyNotFoundException>(() => Row(grid, "Poles")); // Only stored values are shown.
        }
    }

    [Fact]
    public void MixedTemplatesAreShownAsVariousAndUnifiedByOneAssignment()
    {
        var drawing = new Drawing(Device("A", Pair("Power", 1.0)), Object("B", "MOTOR", Pair("Poles", 4)), Empty("C"));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.Equal(ObjectCalculation.Various, grid.TemplateText);
            Assert.True(grid.NeedsTemplate);
            Assert.True(grid.AssignTemplate("DEVICE"));
            Assert.Equal(1, drawing.Commits);
            Assert.Equal("Устройство", grid.TemplateText);
            Assert.Equal(new object?[] { 1.0, null, null }, drawing.Stored("Power"));
            Assert.Equal(new object?[] { null, 4, null }, drawing.Stored("Poles")); // Kept outside the template.
            Assert.Equal(PropertyState.Set, Row(grid, "Poles").State);
        }
    }

    [Fact]
    public void EntityTypesFilterTheTemplatesAndAWrongTypeCancelsTheGroup()
    {
        var line = new EntityDataSnapshot("L", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>());
        var polyline = new EntityDataSnapshot("P", "Polyline", Array.Empty<DataValue>(), Array.Empty<DataRecord>());
        var drawing = new Drawing(line, polyline);
        var (_, grid, controller) = Open(drawing);
        using (controller)
            Assert.Equal(new[] { "CABLE", "TRAY" }, grid.AvailableTemplates.Select(option => option.Key));

        // No template accepts both a block and a line: nothing is offered and nothing is written.
        var mixed = new Drawing(Empty("A"), line);
        var (selection, mixedGrid, mixedController) = Open(mixed);
        using (mixedController)
        {
            Assert.Empty(mixedGrid.AvailableTemplates);
            Assert.False(mixedGrid.AssignTemplate("DEVICE"));
            // The patch checks the filter too: one wrong object cancels the whole transaction (TZXDATA 6.3).
            Assert.False(selection.RequestEdit(SelectionEdit.SetTemplate("DEVICE")));
            Assert.Contains("не применяется к типу Line", mixedGrid.Message);
            Assert.True(mixedGrid.HasError);
            Assert.Equal(0, mixed.Commits);
            Assert.All(mixed.Snapshots, snapshot => Assert.Equal(XDataFormat.None, mixed.Read(snapshot).Format));
        }

        var schema = ParameterSchemaJson.Load(File.ReadAllText(ExamplePath)).Schema!;
        Assert.Equal(new[] { "CABLE", "TRAY" }, schema.TemplatesFor(new[] { "line", "Polyline" }).Select(template => template.Id));
        Assert.Equal(schema.Templates.Count, schema.TemplatesFor(Array.Empty<string>()).Count);
        Assert.True(new SchemaTemplate { Id = "ANY" }.Accepts("Circle")); // No filter.
    }

    [Fact]
    public void CommittingDefaultsWritesOnlyTheMissingTemplateDefaultsInOneTransaction()
    {
        var drawing = new Drawing(
            Object("A", "MOTOR", Pair("Poles", 4), Pair("Legacy", "x")),
            Object("B", "MOTOR", Pair("Device_Type", "Насос")),
            Device("C", Pair("Power", 1.5)),
            Empty("D"));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.True(grid.HasDefaults);
            Assert.True(grid.CommitDefaults());
            Assert.Equal(1, drawing.Commits);
            Assert.Equal(3, drawing.Writes); // D has no template and no defaults: it is not written.
            // Template defaults override parameter defaults; stored values are kept.
            Assert.Equal(new object?[] { "Электродвигатель", "Насос", null, null }, drawing.Stored("Device_Type"));
            Assert.Equal(new object?[] { 4, 3, null, null }, drawing.Stored("Poles"));
            Assert.Equal(new object?[] { "_AC_380V_50Hz", "_AC_380V_50Hz", "_AC_220V_50Hz", null }, drawing.Stored("Voltage"));
            Assert.Equal(new object?[] { "ABC", "ABC", "A", null }, drawing.Stored("Phase"));
            Assert.Equal(new object?[] { 0.0, 0.0, 1.5, null }, drawing.Stored("Power"));
            // Parameters without a default and tables are not written; values outside the template are kept.
            Assert.Equal(new object?[] { null, null, null, null }, drawing.Stored("Current"));
            Assert.Equal(new object?[] { null, null, null, null }, drawing.Stored("Materials"));
            Assert.Equal(new object?[] { "x", null, null, null }, drawing.Stored("Legacy"));
            Assert.Empty(drawing.Snapshots[3].XData);

            Assert.False(grid.HasDefaults);
            Assert.False(grid.CommitDefaultsCommand.CanExecute(null));
            Assert.Equal(PropertyState.Set, Row(grid, "Voltage").State);
            Assert.False(grid.CommitDefaults());
            Assert.Equal(1, drawing.Commits);
        }
    }

    [Fact]
    public void CommittingDefaultsWritesMissingPersistResultsOfTheTemplate()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(ExamplePath));
        result.Schema!.FindParameter("GC_Name")!.Persist = true;
        var cache = new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
        var stale = Device("A", Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "3"));
        var drawing = new Drawing(cache, stale, Device("B", Pair("GC_HeadDevice", "ЩУ2"), Pair("GC_HDGroup", "1"), Pair("GC_Name", "old")));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.True(grid.CommitDefaults());
            Assert.Equal("ЩУ1.3", drawing.Read(drawing.Snapshots[0]).Find("GC_Name")!.Value);
            // A stored result is not a default: it is updated by its sources or by VELEC_CHECK (TZXDATA 7.4).
            Assert.Equal("old", drawing.Read(drawing.Snapshots[1]).Find("GC_Name")!.Value);
        }
    }

    [Fact]
    public void MissingDefaultsFollowTheTemplateOrder()
    {
        var cache = Cache();
        var values = XDataV2.Read(Object("A", "MOTOR", Pair("Voltage", "_AC_220V_50Hz")), cache.Schema);
        var calculation = new ObjectCalculation(values);
        Assert.Equal(new[] { "Device_Type", "Power", "Phase", "Poles" }, calculation.MissingDefaults());
        Assert.Empty(new ObjectCalculation(XDataV2.Read(Empty("B"), cache.Schema)).MissingDefaults());
        calculation.SetTemplate("TRAY");
        Assert.Equal(new[] { "Tray_Width", "Tray_Height", "Tray_Covered" }, calculation.MissingDefaults());
    }

    [Fact]
    public void ReadOnlySelectionsDoNotAssignTemplates()
    {
        var (selection, grid) = Model();
        selection.Apply(new[] { Empty("A") }, cache: Cache());
        Assert.False(grid.CanEdit); // No writer.
        Assert.Empty(grid.AvailableTemplates);
        Assert.False(grid.AssignTemplate("DEVICE"));
        Assert.False(grid.AssignTemplateCommand.CanExecute("DEVICE"));
        Assert.False(grid.CommitDefaults());
    }

    [Fact]
    public void TheCommandPromptOffersTheTemplatesOfTheSelectedTypesByIdOrNumber()
    {
        var cache = Cache();
        var empty = new TemplateApply(cache.Schema, cache.Get(new[] { Empty("A"), Empty("B") }));
        Assert.Equal(new[] { "DEVICE", "MOTOR" }, empty.Templates.Select(template => template.Id));
        Assert.Null(empty.Current);
        Assert.False(empty.HasTemplate);
        Assert.Equal(new[] { "1. Устройство (DEVICE)", "2. Электродвигатель (MOTOR)" }, empty.Lines());
        Assert.Equal("MOTOR", empty.Parse(" motor "));
        Assert.Equal("DEVICE", empty.Parse("1"));
        Assert.Equal("MOTOR", empty.Parse("2"));
        Assert.Null(empty.Parse("3"));
        Assert.Null(empty.Parse("0"));
        Assert.Null(empty.Parse("CABLE")); // CABLE is for lines and polylines.
        Assert.Null(empty.Parse(TemplateApply.None)); // Nothing to remove.
        Assert.Null(empty.Parse(""));
        Assert.Equal(2, empty.Changes("MOTOR"));

        var motors = new TemplateApply(cache.Schema, cache.Get(new[] { WithId("A", Guid.NewGuid().ToString(), "MOTOR"), Object("B", "MOTOR") }));
        Assert.Equal("MOTOR", motors.Current);
        Assert.Equal(new[] { "1. Устройство (DEVICE)", "2. Электродвигатель (MOTOR) *", "-. Без шаблона" }, motors.Lines());
        Assert.Equal("", motors.Parse(TemplateApply.None));
        Assert.Equal(1, motors.Changes("MOTOR")); // B has no DEVICE_ID yet.
        Assert.Equal(2, motors.Changes("DEVICE"));
        Assert.Equal(2, motors.Changes(""));

        var line = new EntityDataSnapshot("L", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>());
        var mixed = new TemplateApply(cache.Schema, cache.Get(new[] { Empty("A"), line }));
        Assert.Empty(mixed.Templates);
        Assert.Empty(mixed.Lines());
    }

    [Fact]
    public void TheCommandAssignsTheChosenTemplateToTheWholeSelectionInOneTransaction()
    {
        // VELEC_TEMPLATE_APPLY writes through XDataWriter as the panel does; the drawing stands in for AutoCAD.
        var drawing = new Drawing(Empty("A"), Device("B", Pair("Device_Class", "Щит"), Pair("Power", 2.5)));
        var prompt = new TemplateApply(drawing.Values.Schema, drawing.Values.Get(drawing.Snapshots));
        var id = prompt.Parse("MOTOR")!;
        drawing.WriteSelection(drawing.Snapshots.Select(snapshot => snapshot.Handle).ToArray(), SelectionEdit.SetTemplate(id));
        Assert.Equal(1, drawing.Commits);
        var values = drawing.Snapshots.Select(drawing.Read).ToArray();
        Assert.All(values, item => Assert.Equal("MOTOR", item.Template));
        Assert.All(values, item => Assert.True(Guid.TryParse(item.DeviceId, out _)));
        Assert.Equal(new object?[] { null, "Щит" }, drawing.Stored("Device_Class")); // Outside MOTOR, kept.
        Assert.Equal(new object?[] { null, 2.5 }, drawing.Stored("Power"));
    }

    private static EntityDataSnapshot Empty(string handle, params DataValue[] xdata) =>
        new EntityDataSnapshot(handle, "BlockReference", xdata, Array.Empty<DataRecord>());

    private static EntityDataSnapshot WithId(string handle, string deviceId, string template, params DataValue[][] pairs)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2) };
        xdata.AddRange(Pair(XDataV2.DeviceIdKey, deviceId));
        xdata.AddRange(Pair(XDataV2.TemplateKey, template));
        foreach (var pair in pairs) xdata.AddRange(pair);
        return new EntityDataSnapshot(handle, "BlockReference", xdata.ToArray(), Array.Empty<DataRecord>());
    }
}
