using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// TZXDATA stage 6: the group editing tests of stage 4 (<see cref="GroupEditingTests"/>) for the v2 VST of the
/// «Свойства» tab. Every edit goes through <see cref="SelectionController"/> and <see cref="XDataWriter"/> into an
/// in-memory drawing, and the tree is rebuilt from the written objects.
/// </summary>
public sealed class GroupEditingV2Tests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [Fact]
    public void ComparesEveryValueAndMaterialFieldIncludingMissingRows()
    {
        var drawing = new Drawing(
            Device("A", Pair("NMO_Name", "One"), Pair("Power", 1.5), Materials(("LineFittings", "СВ110-5", 1, true, "shared"))),
            Device("B", Pair("NMO_Name", "Two"), Pair("Power", 1.5), Materials(("LineFittings", "СВ110-5", 2, false, "shared"))),
            Device("C", Pair("NMO_Name", "One"), Pair("Power", 1.5)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.True(grid.CanEdit);
            Assert.True(Row(grid, "NMO_Name").IsMixed);
            Assert.Equal("", Row(grid, "NMO_Name").EditText);
            Assert.False(Row(grid, "Power").IsMixed);
            Assert.Equal("1.50", Row(grid, "Power").EditText); // The editor starts with the shown value.
            var row = TableRow(grid, "СВ110-5");
            // The row is missing in C: every field of it is «Разное».
            Assert.True(row.IsMixed);
            Assert.Null(row.Flag);
            grid.EditRow(row);
            row = TableRow(grid, "СВ110-5");
            Assert.True(row.IsEditing);
            Assert.All(row.Children, cell => Assert.Equal(PropertyRowKind.Cell, cell.Kind));
            Assert.Equal(new[] { "Mat_Category", "Mat_Mark", "Mat_Count", "Mat_InSpec", "Mat_Comment" }, row.Children.Select(cell => cell.Column));
            Assert.All(row.Children, cell => Assert.True(cell.IsMixed, cell.Column));
            Assert.Equal(0, drawing.Writes);
        }
    }

    [Fact]
    public void ConfirmationWritesOnlyTheChangedValueAndRefreshesAllObjects()
    {
        var drawing = new Drawing(
            Device("A", Pair("NMO_Name", "One"), Pair("Power", 1.0), Materials(("LineFittings", "СВ110-5", 1, true, ""))),
            Device("B", Pair("NMO_Name", "Two"), Pair("Power", 2.0), Materials(("LineFittings", "СВ110-5", 2, false, ""))));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.False(grid.Commit(Row(grid, "NMO_Name")));
            Assert.Equal(0, drawing.Writes); // Merely visiting the mixed value is not a change.
            var name = Row(grid, "NMO_Name");
            name.EditText = "Unified";
            Assert.Equal(0, drawing.Writes);
            Assert.True(grid.Commit(name));
            Assert.Equal(1, drawing.Commits);
            Assert.Equal(2, drawing.Writes); // One transaction for both objects.
            Assert.False(Row(grid, "NMO_Name").IsMixed);
            Assert.Equal("Unified", Row(grid, "NMO_Name").Value);
            Assert.Equal(new object[] { "Unified", "Unified" }, drawing.Stored("NMO_Name"));
            // The other mixed values are kept in every object.
            Assert.Equal(new object[] { 1.0, 2.0 }, drawing.Stored("Power"));
            Assert.Equal(new object[] { 1.0, 2.0 }, drawing.Cells("Materials", "СВ110-5", "Mat_Count"));
            Assert.Equal(new object[] { true, false }, drawing.Cells("Materials", "СВ110-5", "Mat_InSpec"));
            Assert.True(Row(grid, "Power").IsMixed);
            // An unchanged value is not written again.
            var again = Row(grid, "NMO_Name");
            again.EditText = "Unified";
            Assert.False(grid.Commit(again));
            Assert.Equal(1, drawing.Commits);
        }
    }

    [Fact]
    public void NumberAndEnumInputIsConvertedAndCheckedAgainstTheSchema()
    {
        var drawing = new Drawing(Device("A", Pair("Power", 1.0)), Device("B", Pair("Power", 1.0)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            var power = Row(grid, "Power");
            Assert.Equal(PropertyEditorKind.Number, power.Editor);
            Assert.Equal("кВт", power.Unit);
            power.EditText = "2,5";
            Assert.True(grid.Commit(power));
            Assert.Equal(new object[] { 2.5, 2.5 }, drawing.Stored("Power"));
            Assert.Equal("2.50", Row(grid, "Power").Value);
            Assert.False(grid.HasError);

            power = Row(grid, "Power");
            power.EditText = "-1";
            Assert.False(grid.Commit(power));
            Assert.True(grid.HasError);
            Assert.Contains("Power", grid.Message);
            Assert.Equal("2.50", power.EditText); // The editor shows the value again.
            power.EditText = "abc";
            Assert.False(grid.Commit(power));
            Assert.Equal(1, drawing.Commits);

            var voltage = Row(grid, "Voltage");
            Assert.Equal(PropertyEditorKind.Choice, voltage.Editor);
            Assert.Equal(new[] { "220 В", "380 В", "=24 В" }, voltage.Options.Select(option => option.Caption));
            Assert.False(voltage.AllowCustom);
            voltage.EditText = "380 В";
            Assert.True(grid.Commit(voltage));
            Assert.Equal(new object[] { "_AC_380V_50Hz", "_AC_380V_50Hz" }, drawing.Stored("Voltage"));
            Assert.Equal("380 В", Row(grid, "Voltage").Value);
            Assert.False(grid.HasError); // A successful write clears the input error.
            voltage = Row(grid, "Voltage");
            voltage.EditText = "110 В";
            Assert.False(grid.Commit(voltage));
            Assert.Contains("нет в списке", grid.Message);
            Assert.Equal(2, drawing.Commits);
        }
    }

    [Fact]
    public void FormulasAreRecalculatedAtOnceAndCannotBeEdited()
    {
        var drawing = new Drawing(
            Device("A", Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "3")),
            Device("B", Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "4")));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.Equal(ObjectCalculation.Various, Row(grid, "GC_Name").Value);
            Assert.False(Row(grid, "GC_Name").IsEditable);
            var group = Row(grid, "GC_HDGroup");
            group.EditText = "7";
            Assert.True(grid.Commit(group));
            Assert.Equal("ЩУ1.7", Row(grid, "GC_Name").Value);
            Assert.False(Row(grid, "GC_Name").IsMixed);
        }
    }

    [Fact]
    public void MaterialChangeAddAndDeleteApplyToTheWholeSelection()
    {
        var drawing = new Drawing(
            Device("A", Materials(("LineFittings", "СВ110-5", 1, true, ""))),
            Device("B", Materials(("LineFittings", "СВ110-5", 2, true, ""))));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            // Column 2 of a table row edits its count.
            var row = TableRow(grid, "СВ110-5");
            Assert.Equal("Mat_Count", row.Column);
            Assert.True(row.IsMixed);
            row.EditText = "7";
            Assert.True(grid.Commit(row));
            Assert.Equal(new object[] { 7.0, 7.0 }, drawing.Cells("Materials", "СВ110-5", "Mat_Count"));
            Assert.Equal("7", TableRow(grid, "СВ110-5").Value);

            // The check box of the flag column applies on click.
            Assert.True(grid.SetFlag(TableRow(grid, "СВ110-5"), false));
            Assert.Equal(new object[] { false, false }, drawing.Cells("Materials", "СВ110-5", "Mat_InSpec"));

            // «+ Добавить» of a category adds a row with a new key and the category to every object.
            var category = grid.Find("t:MATERIALS")!.Children.Single();
            Assert.True(category.CanAdd);
            Assert.True(grid.AddRow(category));
            Assert.Equal(new object[] { "LineFittings", "LineFittings" }, drawing.Cells("Materials", PropertyGridViewModel.NewRowKey, "Mat_Category"));
            var added = TableRow(grid, PropertyGridViewModel.NewRowKey);
            Assert.True(added.IsEditing);
            Assert.Equal("Mat_Mark", grid.SelectedRow?.Column);
            Assert.Contains(grid.SelectedRow!, grid.Rows);
            Assert.True(grid.AddRow(grid.Find("t:MATERIALS")!));
            Assert.Equal(3, drawing.Rows("Materials").Count);
            Assert.NotNull(TableRow(grid, PropertyGridViewModel.NewRowKey + " 2"));

            // ✎ cell of the key column renames the row; it stays open and selected.
            var key = TableRow(grid, PropertyGridViewModel.NewRowKey).Children.Single(cell => cell.Column == "Mat_Mark");
            grid.SelectedRow = key;
            key.EditText = "ПС70";
            Assert.True(grid.Commit(key));
            Assert.Equal(new[] { "СВ110-5", "ПС70", PropertyGridViewModel.NewRowKey + " 2" }, drawing.Rows("Materials"));
            Assert.True(TableRow(grid, "ПС70").IsEditing);
            Assert.Equal("ПС70", grid.SelectedRow?.Value);
            // A duplicate key is refused.
            key = TableRow(grid, "ПС70").Children.Single(cell => cell.Column == "Mat_Mark");
            key.EditText = "СВ110-5";
            Assert.False(grid.Commit(key));
            Assert.Contains("уже есть", grid.Message);

            // × removes the row from every object.
            Assert.True(grid.DeleteRow(TableRow(grid, "СВ110-5")));
            Assert.Equal(new[] { "ПС70", PropertyGridViewModel.NewRowKey + " 2" }, drawing.Rows("Materials"));
            Assert.All(drawing.Snapshots, snapshot => Assert.Equal(2, drawing.Read(snapshot).Find("Materials")!.Rows!.Count));
        }
    }

    [Fact]
    public void FailedWriteRestoresValuesAndReportsErrorWithoutLosingSelection()
    {
        var drawing = new Drawing(Device("A", Pair("NMO_Name", "One"))) { FailWrite = true };
        var (selection, grid, controller) = Open(drawing);
        var name = Row(grid, "NMO_Name");
        name.EditText = "Rejected";
        Assert.False(grid.Commit(name));
        Assert.Equal("One", Row(grid, "NMO_Name").Value);
        Assert.Equal("One", Row(grid, "NMO_Name").EditText);
        Assert.True(grid.HasError);
        Assert.Contains("write failed", grid.Message);
        Assert.True(selection.HasSelection);
        Assert.Equal(new object[] { "One" }, drawing.Stored("NMO_Name"));
        drawing.FailWrite = false;
        name = Row(grid, "NMO_Name");
        name.EditText = "Accepted";
        Assert.True(grid.Commit(name));
        Assert.False(grid.HasError);
        controller.Dispose();
        Assert.False(grid.CanEdit);
        Assert.False(Row(grid, "NMO_Name").IsEditable);
    }

    [Fact]
    public void LiteralMixedTextIsDataAndEmptyTextRestoresTheDefaultOfANumber()
    {
        var drawing = new Drawing(Device("A", Pair("NMO_Name", "Разное"), Pair("Power", 1.0)), Device("B", Pair("NMO_Name", "Two"), Pair("Power", 2.0)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            var name = Row(grid, "NMO_Name");
            name.EditText = "";
            Assert.True(grid.Commit(name));
            Assert.False(Row(grid, "NMO_Name").IsMixed);
            Assert.Equal(new object[] { "", "" }, drawing.Stored("NMO_Name"));
            name = Row(grid, "NMO_Name");
            name.EditText = ObjectCalculation.Various;
            Assert.True(grid.Commit(name));
            Assert.Equal(ObjectCalculation.Various, Row(grid, "NMO_Name").Value);
            Assert.False(Row(grid, "NMO_Name").IsMixed);
            Assert.Equal(new object[] { "Разное", "Разное" }, drawing.Stored("NMO_Name"));

            var power = Row(grid, "Power");
            power.EditText = " ";
            Assert.True(grid.Commit(power));
            Assert.Equal(new object?[] { null, null }, drawing.Stored("Power"));
            Assert.Equal("○", Row(grid, "Power").StateText);
            Assert.True(Row(grid, "Power").IsDefault);
        }
    }

    [Fact]
    public void CheckboxAppliesTheClickedStateAndRetainsOtherMixedValues()
    {
        var drawing = new Drawing(
            Object("A", "TRAY", Pair("Tray_Covered", (short)1), Pair("Tray_Width", 100)),
            Object("B", "TRAY", Pair("Tray_Width", 200)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            var covered = Row(grid, "Tray_Covered");
            Assert.Equal(PropertyEditorKind.Flag, covered.Editor);
            Assert.True(covered.CanSetFlag);
            Assert.True(covered.IsMixed);
            Assert.Null(covered.Flag); // Three-state for «Разное».
            Assert.False(grid.Commit(covered)); // A check box is not written by Enter.
            Assert.True(grid.SetFlag(covered, true));
            Assert.Equal(new object[] { true, true }, drawing.Stored("Tray_Covered"));
            Assert.True(Row(grid, "Tray_Covered").Flag);
            Assert.False(grid.SetFlag(Row(grid, "Tray_Covered"), true)); // No change, no write.
            Assert.True(grid.SetFlag(Row(grid, "Tray_Covered"), false));
            Assert.False(Row(grid, "Tray_Covered").Flag);
            Assert.Equal(2, drawing.Commits);
            Assert.True(Row(grid, "Tray_Width").IsMixed);
        }
    }

    [Fact]
    public void AddParameterWritesTheDefaultAndRemovesValuesOutsideTheTemplate()
    {
        var drawing = new Drawing(
            Device("A", Pair("Power", 1.0), Pair("Legacy", "x")),
            Device("B", Pair("Power", 2.0)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.Contains(grid.AvailableParameters, option => option.Key == "Poles");
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Power"); // Shown already.
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Materials"); // Tables are not scalars.
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Mat_Count"); // Table columns.
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "GC_Name"); // Formulas.
            Assert.Equal("Число полюсов (Poles)", grid.AvailableParameters.Single(option => option.Key == "Poles").Caption);
            Assert.False(grid.AddParameter("Unknown"));
            Assert.True(grid.AddParameter("Poles"));
            Assert.Equal(new object[] { 1, 1 }, drawing.Stored("Poles"));
            var poles = Row(grid, "Poles");
            Assert.Same(poles, grid.SelectedRow);
            Assert.Contains(poles, grid.Rows);
            Assert.True(poles.CanDelete);
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Poles");
            Assert.True(grid.DeleteRow(poles));
            Assert.Equal(new object?[] { null, null }, drawing.Stored("Poles"));

            // A value outside the schema has × in «Не описаны в схеме».
            var legacy = Row(grid, "Legacy");
            Assert.True(legacy.CanDelete);
            Assert.False(Row(grid, "Power").CanDelete); // A parameter of the template is cleared, not removed.
            grid.AddParameterCommand.Execute(grid.AvailableParameters.Single(option => option.Key == "Tray_Covered"));
            Assert.Equal(new object[] { false, false }, drawing.Stored("Tray_Covered"));
            Assert.True(grid.DeleteRow(Row(grid, "Legacy")));
            Assert.Equal(new object?[] { null, null }, drawing.Stored("Legacy"));
            Assert.Null(grid.Find("g:" + PropertyGridViewModel.GroupNotInSchema));
        }
    }

    [Fact]
    public void ReadOnlySourcesAndLegacyObjectsAreNotEditable()
    {
        var (selection, grid) = Model();
        selection.Apply(new[] { Device("A", Pair("Power", 1.0)) }, cache: Cache());
        Assert.False(grid.CanEdit); // No writer.
        Assert.False(Row(grid, "Power").IsEditable);

        var legacy = new EntityDataSnapshot("L", "BlockReference", new[] { V(1001, DrawingNames.RegApp), V(1000, "Type=Кабель") }, Array.Empty<DataRecord>());
        var drawing = new Drawing(Device("A", Pair("Power", 1.0)), legacy);
        var (_, mixed, controller) = Open(drawing);
        using (controller)
        {
            Assert.False(mixed.CanEdit);
            Assert.Contains("только чтение", mixed.Message);
            Assert.False(mixed.AddParameter("Poles"));
        }
    }

    private static (SelectionViewModel, PropertyGridViewModel, SelectionController) Open(Drawing drawing)
    {
        var (selection, grid) = Model();
        return (selection, grid, new SelectionController(drawing, selection));
    }

    private static (SelectionViewModel, PropertyGridViewModel) Model()
    {
        var selection = new SelectionViewModel(new PropertyEditorViewModel());
        return (selection, new PropertyGridViewModel(selection, CultureInfo.InvariantCulture));
    }

    private static IEnumerable<PropertyRow> All(IEnumerable<PropertyRow> rows) => rows.SelectMany(row => new[] { row }.Concat(All(row.Children)));

    private static PropertyRow Row(PropertyGridViewModel grid, string id) =>
        All(grid.Roots).FirstOrDefault(row => row.Id == id && row.Kind == PropertyRowKind.Parameter) ?? throw new KeyNotFoundException(id);

    private static PropertyRow TableRow(PropertyGridViewModel grid, string key) =>
        All(grid.Roots).FirstOrDefault(row => row.Kind == PropertyRowKind.TableRow && row.RowKey == key) ?? throw new KeyNotFoundException(key);

    private static ObjectValuesCache Cache()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(ExamplePath));
        Assert.NotNull(result.Schema);
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }

    private static EntityDataSnapshot Device(string handle, params DataValue[][] pairs) => Object(handle, "DEVICE", pairs);

    private static EntityDataSnapshot Object(string handle, string template, params DataValue[][] pairs)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2) };
        xdata.AddRange(Pair(XDataV2.TemplateKey, template));
        foreach (var pair in pairs) xdata.AddRange(pair);
        return new EntityDataSnapshot(handle, "BlockReference", xdata.ToArray(), Array.Empty<DataRecord>());
    }

    private static DataValue[] Pair(string id, object value) => new[]
    {
        V(1000, id),
        value switch
        {
            string text => V(1000, text),
            double number => V(1040, number),
            int integer => V(1071, integer),
            short flag => V(1070, flag),
            _ => throw new ArgumentException(value.GetType().Name)
        }
    };

    private static DataValue[] Materials(params (string Category, string Mark, double Count, bool InSpec, string Comment)[] rows)
    {
        var list = new List<DataValue> { V(1000, "Materials"), V(1002, "{") };
        foreach (var row in rows)
        {
            list.Add(V(1002, "{"));
            list.AddRange(Pair("Mat_Category", row.Category));
            list.AddRange(Pair("Mat_Mark", row.Mark));
            list.AddRange(Pair("Mat_Count", row.Count));
            list.AddRange(Pair("Mat_InSpec", (short)(row.InSpec ? 1 : 0)));
            if (row.Comment.Length != 0) list.AddRange(Pair("Mat_Comment", row.Comment));
            list.Add(V(1002, "}"));
        }
        list.Add(V(1002, "}"));
        return list.ToArray();
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);

    /// <summary>The selection of a drawing written through <see cref="XDataWriter"/> in one transaction per edit.</summary>
    private sealed class Drawing : ISelectionSource, ISelectionWriter, IObjectValuesSource
    {
        private readonly XDataWriter writer;

        public Drawing(params EntityDataSnapshot[] snapshots)
        {
            Snapshots = snapshots;
            Values = Cache();
            writer = new XDataWriter(Begin, Values);
        }

        public EntityDataSnapshot[] Snapshots { get; private set; }
        public int Writes { get; private set; }
        public int Commits { get; private set; }
        /// <summary>The transaction fails on commit: nothing is written (the AutoCAD transaction is aborted).</summary>
        public bool FailWrite { get; set; }
        public ObjectValuesCache Values { get; }
        ObjectValuesCache? IObjectValuesSource.Values => Values;
        public event EventHandler? SelectionChanged { add { } remove { } }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() => Snapshots;
        public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit) => writer.WriteSelection(handles, edit);

        public ObjectValues Read(EntityDataSnapshot snapshot) => XDataV2.Read(snapshot, Values.Schema);

        /// <summary>The typed value of a parameter in every object; null when it is not stored.</summary>
        public object?[] Stored(string id) => Snapshots.Select(snapshot => Read(snapshot).Find(id)?.Value).ToArray();

        /// <summary>A cell of the row with <paramref name="key"/> in every object; null when the row or cell is missing.</summary>
        public object?[] Cells(string table, string key, string column) =>
            Snapshots.Select(snapshot => Read(snapshot).Find(table)?.Rows?.FirstOrDefault(row => row.Key == key)?.Find(column)?.Value).ToArray();

        /// <summary>Row keys of the table in the first object.</summary>
        public IReadOnlyList<string?> Rows(string table) => Read(Snapshots[0]).Find(table)!.Rows!.Select(row => row.Key).ToArray();

        private IXDataWriteTransaction Begin(IReadOnlyList<string> handles) => new Transaction(this, handles);

        private sealed class Transaction : IXDataWriteTransaction
        {
            private readonly Drawing drawing;
            private readonly EntityDataSnapshot[] staged;

            public Transaction(Drawing drawing, IReadOnlyList<string> handles)
            {
                this.drawing = drawing;
                staged = drawing.Snapshots.ToArray();
                Snapshots = handles.Select(handle => drawing.Snapshots.Single(s => s.Handle == handle)).ToArray();
            }

            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }

            public void Write(EntityDataSnapshot snapshot)
            {
                drawing.Writes++;
                staged[Array.FindIndex(staged, s => s.Handle == snapshot.Handle)] = snapshot;
            }

            public void Commit()
            {
                if (drawing.FailWrite) throw new InvalidOperationException("write failed");
                drawing.Snapshots = staged;
                drawing.Commits++;
            }

            public void Dispose() { }
        }
    }
}
