using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Xunit;
using Xunit.Abstractions;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>The read-only VST of the «Свойства» tab (TZXDATA 8, stage 5).</summary>
public sealed class PropertyGridTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");
    private readonly ITestOutputHelper output;

    public PropertyGridTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void BuildsGroupsInTemplateOrderWithStatesUnitsAndDefaults()
    {
        var grid = Grid(Device("A", Pair("Power", 1.5), Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "3"), Pair("Phase", "ABC")));
        Dump(grid);
        Assert.True(grid.IsEnabled);
        Assert.Equal("Устройство", grid.TemplateText);
        Assert.False(grid.NeedsTemplate);
        Assert.Equal("", grid.Message);
        // Groups in the order of the template; a group of one table with its caption is the table node itself (8.1).
        Assert.Equal(new[] { "Общие", "Обозначение", "Электрика", "Материалы" }, grid.Roots.Select(row => row.Caption));
        Assert.Equal(PropertyRowKind.Table, grid.Roots[3].Kind);
        Assert.All(grid.Roots.Take(3), row => Assert.True(row.IsHeader && row.IsExpanded));
        Assert.Equal(new[] { "Мощность, кВт", "Ток, А", "Напряжение", "Фаза" }, grid.Find("g:Электрика")!.Children.Select(row => row.Caption));

        var power = Row(grid, "Power");
        Assert.Equal("1.50", power.Value);
        Assert.Equal("●", power.StateText);
        Assert.False(power.IsDefault);
        Assert.Equal("Phase", Row(grid, "Phase").Id);
        Assert.Equal("ABC (3 фазы)", Row(grid, "Phase").Value);
        // Defaults of the parameter (6.2): grey, ○ and «по умолчанию».
        var voltage = Row(grid, "Voltage");
        Assert.Equal("220 В", voltage.Value);
        Assert.Equal("○", voltage.StateText);
        Assert.True(voltage.IsDefault);
        Assert.Equal(PropertyGridViewModel.DefaultNote, voltage.Note);
        // CalculatedString: ƒ with the result of the formula and the description.
        var name = Row(grid, "GC_Name");
        Assert.Equal("ЩУ1.3", name.Value);
        Assert.Equal("ƒ", name.StateText);
        Assert.Equal("вычисляемое", name.Note);
        Assert.Contains("@@[GC_HeadDevice].@@[GC_HDGroup]", name.ToolTip);
        // The visible rows are the flattened expanded tree.
        Assert.Equal(grid.RowCount, grid.Rows.Count);
        Assert.Equal(grid.Roots[0], grid.Rows[0]);
    }

    [Fact]
    public void TemplateDefaultsOverrideParameterDefaults()
    {
        var grid = Grid(Object("M", "MOTOR"));
        Assert.Equal("Электродвигатель", grid.TemplateText);
        Assert.Equal("380 В", Row(grid, "Voltage").Value);
        Assert.Equal("3", Row(grid, "Poles").Value);
        Assert.Equal("Электродвигатель", Row(grid, "Device_Type").Value);
        Assert.True(Row(grid, "Device_Type").IsDefault);
    }

    [Fact]
    public void TableRowsAreGroupedByCategoryAndMappedByVstColumn()
    {
        var grid = Grid(Device("A", Materials(
            ("LineFittings", "СВ110-5", 2.0, true, "опора"),
            ("LineFittings", "ПС70", 6.0, false, ""),
            ("", "Болт", 4.0, true, ""))));
        Dump(grid);
        var table = grid.Find("t:MATERIALS")!;
        Assert.Equal("Материалы", table.Caption);
        Assert.Equal("3 строки", table.Value);
        Assert.Equal("●", table.StateText);
        Assert.True(table.ShowAdd);
        Assert.False(table.ShowActions);
        Assert.Contains("Колонки: Категория, Марка, Кол., В спецификацию, Примечание", table.ToolTip);
        Assert.Equal(new[] { "Линейная арматура", PropertyGridViewModel.NoCategory }, table.Children.Select(row => row.Caption));
        var category = table.Children[0];
        Assert.Equal(PropertyRowKind.Category, category.Kind);
        Assert.Equal("2 строки", category.Value);
        var row = category.Children[0];
        Assert.Equal(PropertyRowKind.TableRow, row.Kind);
        Assert.Equal(2, row.Level);
        Assert.True(row.ShowActions);
        Assert.Equal("СВ110-5", row.Caption);
        Assert.Equal("2", row.Value);
        Assert.True(row.HasFlag);
        Assert.True(row.Flag);
        Assert.Equal("опора", row.Note);
        Assert.False(category.Children[1].Flag);
        Assert.Contains("Категория: Линейная арматура", row.ToolTip);
        Assert.Equal("Болт", table.Children[1].Children.Single().Caption);
        // An empty stored table and a table of the template that is not stored.
        Assert.Equal("0 строк", Grid(Device("B", Pair("Materials", Array.Empty<DataValue>()))).Find("t:MATERIALS")!.Value);
        var missing = Grid(Device("C")).Find("t:MATERIALS")!;
        Assert.Equal("0 строк", missing.Value);
        Assert.Equal("○", missing.StateText);
        Assert.False(missing.IsExpandable);
    }

    [Fact]
    public void MultipleSelectionShowsTheUnionAndDifferingValuesAsMixed()
    {
        var grid = Grid(
            Device("A", Pair("Power", 1.5), Pair("Device_Type", "Щит"), Materials(("LineFittings", "СВ110-5", 2.0, true, ""), ("Concrete", "ПС70", 1.0, true, ""))),
            Device("B", Pair("Power", 2.0), Pair("Device_Type", "Щит"), Materials(("LineFittings", "СВ110-5", 3.0, true, ""))));
        Dump(grid);
        Assert.Equal("Устройство", grid.TemplateText);
        var power = Row(grid, "Power");
        Assert.Equal(ObjectCalculation.Various, power.Value);
        Assert.True(power.IsMixed);
        Assert.Equal("●", power.StateText);
        Assert.Equal("Щит", Row(grid, "Device_Type").Value);
        Assert.False(Row(grid, "Device_Type").IsMixed);
        // Rows are matched by the key column; a different count is «Разное», a row of one object is «Разное».
        var table = grid.Find("t:MATERIALS")!;
        Assert.True(table.IsMixed);
        Assert.Equal(ObjectCalculation.Various, table.Value);
        var shared = table.Children[0].Children.Single();
        Assert.Equal("СВ110-5", shared.Caption);
        Assert.False(shared.IsCaptionMixed);
        Assert.Equal(ObjectCalculation.Various, shared.Value);
        Assert.True(shared.IsMixed);
        Assert.True(shared.Flag);
        var single = table.Children[1].Children.Single();
        Assert.Equal(ObjectCalculation.Various, single.Caption);
        Assert.True(single.IsCaptionMixed);
        Assert.Null(single.Flag);
        Assert.Equal(ObjectCalculation.Various, table.Children[1].Caption);
    }

    [Fact]
    public void DifferentTemplatesGiveTheUnionAndMissingParametersAsMixed()
    {
        var grid = Grid(Device("A", Pair("Voltage", "_DC_24V")), Object("B", "CABLE", Pair("Cable_Mark", "ВВГ"), Pair("Voltage", "_DC_24V")));
        Dump(grid);
        Assert.Equal(ObjectCalculation.Various, grid.TemplateText);
        Assert.Equal("CABLE|DEVICE", grid.TemplateKey);
        Assert.Contains(grid.Roots, row => row.Caption == "Кабель");
        Assert.Equal(ObjectCalculation.Various, Row(grid, "Cable_Mark").Value);
        Assert.Equal(ObjectCalculation.Various, Row(grid, "Power").Value);
        Assert.Equal("=24 В", Row(grid, "Voltage").Value);
        Assert.Equal("●", Row(grid, "Voltage").StateText);
    }

    [Fact]
    public void ValuesOutsideTheSchemaAndTheTemplateGetTheirOwnGroups()
    {
        var grid = Grid(Device("A", Pair("Tray_Width", 200), Pair("Old_Param", "x"), Pair("Old_List", new[] { V(1002, "{"), V(1000, "A"), V(1000, "1"), V(1002, "}") })));
        Dump(grid);
        Assert.Equal(new[] { "Общие", "Обозначение", "Электрика", "Материалы", PropertyGridViewModel.GroupExtra, PropertyGridViewModel.GroupNotInSchema + " (2)" },
            grid.Roots.Select(row => row.Caption));
        Assert.Equal("200", Row(grid, "Tray_Width").Value);
        var outside = grid.Roots.Last();
        Assert.False(outside.IsExpanded);
        Assert.Equal("!", outside.StateText);
        Assert.DoesNotContain(grid.Rows, row => row.Id == "Old_Param");
        var old = Row(grid, "Old_Param");
        Assert.Equal("x", old.Value);
        Assert.Equal("!", old.StateText);
        Assert.Equal("не описан в схеме", old.Note);
        var list = outside.Children[1];
        Assert.Equal(PropertyRowKind.Table, list.Kind);
        Assert.Equal("1 строка", list.Value);
        // Cells of a list outside the schema are mapped in their order: the first is column 1.
        Assert.Equal("1", list.Children.Single().Caption);
        Assert.False(list.ShowAdd);
    }

    [Fact]
    public void WarningsOfStoredValuesAreErrors()
    {
        var grid = Grid(Device("A", Pair("Voltage", "_AC_999V"), Pair("Power", "много"), Pair("Poles", 2)));
        Dump(grid);
        var voltage = Row(grid, "Voltage");
        Assert.Equal("_AC_999V", voltage.Value);
        Assert.Equal("!", voltage.StateText);
        Assert.True(voltage.IsError);
        Assert.NotEqual("", voltage.Note);
        var power = Row(grid, "Power");
        Assert.Equal("много", power.Value);
        Assert.Equal("!", power.StateText);
        Assert.Contains(power.Note, power.ToolTip);
        // An object without TEMPLATE shows only its stored values (6.6): Poles is not in DEVICE.
        Assert.Equal("2", Row(Grid(Object("B", null, Pair("Poles", 2))), "Poles").Value);
        Assert.Single(Grid(Object("B", null, Pair("Poles", 2))).Roots);
    }

    [Fact]
    public void FormulaErrorsAreShownWithTheMessage()
    {
        var json = ParameterSchemaJsonTests.Json(@"{ 'format': 'BobrovXDATA.schema', 'version': 1, 'parameters': [
            { 'id': 'A', 'type': 'String' },
            { 'id': 'F', 'type': 'CalculatedString', 'format': '@@[A].@@[Missing]' } ],
          'templates': [ { 'id': 'T', 'parameters': [ 'A', 'F' ] } ] }");
        // The import refuses such a schema (SchemaValidator); a damaged NOD still gives it to the palette.
        var grid = Grid(json, allowErrors: true, Object("A", "T", Pair("A", "x")));
        var formula = Row(grid, "F");
        Assert.Equal("!", formula.StateText);
        Assert.Contains("@@[Missing]", formula.Note);
    }

    [Fact]
    public void EmptySelectionAndObjectsWithoutBobrovXDataAreExplained()
    {
        var model = new PaletteViewModel();
        Assert.False(model.Grid.IsEnabled);
        Assert.Empty(model.Grid.Rows);
        Assert.Equal("Выберите объекты в чертеже", model.Grid.Message);
        Assert.Same(model.Grid, model.Tabs[0].Content);

        var none = Grid(Raw("A", "Line"));
        Assert.True(none.IsEnabled);
        Assert.True(none.NeedsTemplate);
        Assert.Empty(none.Rows);
        Assert.Equal("нет", none.TemplateText);
        Assert.Contains("назначьте шаблон", none.Message);
        Assert.False(none.AssignTemplateCommand.CanExecute(null));

        var some = Grid(Device("A", Pair("Power", 1.0)), Raw("B", "Line"));
        Assert.True(some.NeedsTemplate);
        Assert.Equal(ObjectCalculation.Various, some.TemplateText);
        Assert.Equal(ObjectCalculation.Various, Row(some, "Power").Value);
        Assert.Contains("без BobrovXDATA: 1", some.Message);

        // Without a value cache only the earlier formats are shown.
        var selection = new SelectionViewModel(new PropertyEditorViewModel());
        var grid = new PropertyGridViewModel(selection, CultureInfo.InvariantCulture);
        selection.Apply(new[] { Device("A", Pair("Power", 1.0)) });
        Assert.Empty(grid.Rows);
        Assert.Contains("нет схемы", grid.Message);
    }

    [Fact]
    public void EarlierFormatsAreShownReadOnlyUntilMigration()
    {
        var legacy = new EntityDataSnapshot("L1", "BlockReference", new[]
        {
            V(1001, "ESMT_LEP_v1.0"), V(1000, "Name=АО21"), V(1000, "Material"), V(1002, "{"),
            V(1000, "Category=Железобетонные элементы"), V(1000, "Name=СВ110-5"), V(1000, "Count=1,2"), V(1000, "IsInSpec=0"), V(1002, "}")
        }, Array.Empty<DataRecord>());
        var grid = Grid(legacy);
        Dump(grid);
        var group = Assert.Single(grid.Roots);
        Assert.Equal(PropertyGridViewModel.GroupLegacy, group.Caption);
        Assert.Contains("VELEC_MIGRATE", group.Note);
        Assert.Equal("АО21", group.Children[0].Value);
        var material = group.Children[1].Children.Single().Children.Single();
        Assert.Equal("Железобетонные элементы", group.Children[1].Children.Single().Caption);
        Assert.Equal("СВ110-5", material.Caption);
        Assert.Equal("1,2", material.Value);
        Assert.False(material.Flag);
        // A legacy object is not «without data».
        Assert.False(grid.NeedsTemplate);
    }

    [Fact]
    public void FilterShowsMatchesWithTheirAncestors()
    {
        var grid = Grid(Device("A", Pair("Power", 1.5), Materials(("LineFittings", "СВ110-5", 2.0, true, ""), ("Concrete", "ПС70", 1.0, true, ""))));
        grid.Filter = "мощн";
        Assert.Equal(new[] { "Электрика", "Мощность, кВт" }, grid.Rows.Select(row => row.Caption));
        grid.Filter = "current";
        Assert.Equal(new[] { "Электрика", "Ток, А" }, grid.Rows.Select(row => row.Caption));
        grid.Filter = "пс70";
        Assert.Equal(new[] { "Материалы", "Железобетонные элементы", "ПС70" }, grid.Rows.Select(row => row.Caption));
        grid.Filter = "Линейная";
        Assert.Equal(new[] { "Материалы", "Линейная арматура", "СВ110-5" }, grid.Rows.Select(row => row.Caption));
        grid.Filter = "нет такого";
        Assert.Empty(grid.Rows);
        grid.Filter = "";
        Assert.Equal(grid.RowCount, grid.Rows.Count);
    }

    [Fact]
    public void ToggleAndExpandAllKeepTheFlattenedRowsConsistent()
    {
        var grid = Grid(Device("A", Materials(("LineFittings", "СВ110-5", 2.0, true, ""), ("Concrete", "ПС70", 1.0, true, ""))));
        var all = grid.Rows.ToArray();
        var group = grid.Find("g:Электрика")!;
        grid.ToggleCommand.Execute(group);
        Assert.False(group.IsExpanded);
        Assert.Equal(all.Length - group.Children.Count, grid.Rows.Count);
        Assert.Equal(Flattened(grid), grid.Rows);
        grid.Toggle(grid.Find("t:MATERIALS")!.Children[0]);
        Assert.Equal(Flattened(grid), grid.Rows);
        grid.ToggleCommand.Execute(group);
        Assert.Equal(Flattened(grid), grid.Rows);
        grid.CollapseAllCommand.Execute(null);
        Assert.Equal(grid.Roots, grid.Rows);
        grid.ExpandAllCommand.Execute(null);
        Assert.Equal(all, grid.Rows);
        // A parameter is not expandable.
        grid.Toggle(Row(grid, "Power"));
        Assert.Equal(all, grid.Rows);
    }

    [Fact]
    public void ExpansionAndScrollAreKeptForSelectionsOfTheSameTemplate()
    {
        var (selection, grid) = Model();
        selection.Apply(new[] { Device("A", Pair("Power", 1.0)) }, cache: Cache());
        grid.Toggle(grid.Find("g:Электрика")!);
        grid.ScrollOffset = 120;
        var notified = new List<string?>();
        grid.PropertyChanged += (_, args) => notified.Add(args.PropertyName);
        selection.Apply(new[] { Device("B", Pair("Power", 2.0)) }, cache: Cache());
        Assert.Contains(nameof(PropertyGridViewModel.Rows), notified);
        Assert.False(grid.Find("g:Электрика")!.IsExpanded);
        Assert.Equal(120, grid.ScrollOffset);
        // Another template starts from the defaults; returning restores the saved state.
        selection.Apply(new[] { Object("C", "CABLE") }, cache: Cache());
        Assert.Equal(0, grid.ScrollOffset);
        Assert.True(grid.Roots.Where(row => row.IsExpandable).All(row => row.IsExpanded));
        selection.Apply(new[] { Device("D") }, cache: Cache());
        Assert.False(grid.Find("g:Электрика")!.IsExpanded);
        Assert.Equal(120, grid.ScrollOffset);
    }

    [Fact]
    public void RefreshRereadsTheSelectionThroughTheController()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = new[] { Device("A", Pair("Power", 1.0)) } };
        using (new SelectionController(source, model.Selection))
        {
            Assert.Equal(1, source.Reads);
            Assert.Equal("1.00", Row(model.Grid, "Power").Value.Replace(',', '.'));
            source.Snapshot = new[] { Device("A", Pair("Power", 3.0)) };
            var built = 0;
            model.Grid.Built += (_, _) => built++;
            model.Grid.RefreshCommand.Execute(null);
            Assert.Equal(2, source.Reads);
            Assert.Equal(1, built);
            Assert.Equal("3.00", Row(model.Grid, "Power").Value.Replace(',', '.'));
            Assert.Contains("VST: объектов 1", model.Grid.BuildSummary);
        }
        // After the controller is disposed ⟳ only rebuilds the tree.
        model.Grid.RefreshCommand.Execute(null);
        Assert.Equal(2, source.Reads);
    }

    [Theory]
    [InlineData(0, "0 строк")]
    [InlineData(1, "1 строка")]
    [InlineData(3, "3 строки")]
    [InlineData(5, "5 строк")]
    [InlineData(11, "11 строк")]
    [InlineData(12, "12 строк")]
    [InlineData(21, "21 строка")]
    [InlineData(104, "104 строки")]
    public void RowCountersUseRussianPlurals(int count, string text) => Assert.Equal(text, PropertyGridViewModel.RowsText(count));

    /// <summary>TZXDATA 10.6: 1000 objects × 50 parameters are built in at most 300 ms after the snapshots are read.</summary>
    [Fact]
    public void ThousandObjectsWithFiftyParametersAreBuiltWithinTheBudget()
    {
        var parameters = new StringBuilder();
        var ids = new List<string>();
        string[] types = { "String", "Double", "Integer", "Boolean", "Enum" };
        for (var i = 0; i < 50; i++)
        {
            var type = types[i % types.Length];
            ids.Add("'P" + i + "'");
            parameters.Append("{ 'id': 'P" + i + "', 'type': '" + type + "', 'group': 'G" + (i / 10) + "', 'caption': 'Параметр " + i + "'" +
                              (type == "Enum" ? ", 'enum': 'E'" : "") + (type == "Double" ? ", 'editor': { 'unit': 'м', 'precision': 2 }" : "") + " },");
        }
        var json = ParameterSchemaJsonTests.Json("{ 'format': 'BobrovXDATA.schema', 'version': 1, 'parameters': [" + parameters.ToString().TrimEnd(',') + "]," +
            " 'enums': [ { 'id': 'E', 'items': [ { 'key': 'a', 'caption': 'А' }, { 'key': 'b', 'caption': 'Б' } ] } ]," +
            " 'templates': [ { 'id': 'T', 'parameters': [" + string.Join(",", ids) + "] } ] }");
        var snapshots = Enumerable.Range(0, 1000).Select(n => Object("H" + n, "T", Enumerable.Range(0, 50).Select(i => (i % 5) switch
        {
            0 => Pair("P" + i, "s" + (n % 3)),
            1 => Pair("P" + i, n * 0.5),
            2 => Pair("P" + i, i),
            3 => Pair("P" + i, (short)(n % 2)),
            _ => Pair("P" + i, "a")
        }).ToArray())).ToArray();
        var cache = Cache(json);
        var (selection, grid) = Model();
        selection.Apply(snapshots, cache: cache);
        selection.Apply(snapshots, cache: cache);
        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            selection.Apply(snapshots, cache: cache);
            if (grid.BuildTime < best) best = grid.BuildTime;
        }
        output.WriteLine(grid.BuildSummary + " Лучшее: " + best.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " мс.");
        Assert.Equal(55, grid.RowCount);
        Assert.Equal(ObjectCalculation.Various, Row(grid, "P0").Value);
        Assert.Equal("2", Row(grid, "P2").Value);
        Assert.Equal("А", Row(grid, "P4").Value);
        Assert.True(best.TotalMilliseconds <= 300, "Построение VST заняло " + best.TotalMilliseconds + " мс.");
    }

    private void Dump(PropertyGridViewModel grid)
    {
        foreach (var row in Flattened(grid, all: true)) output.WriteLine(row.ToString());
    }

    private static IEnumerable<PropertyRow> Flattened(PropertyGridViewModel grid, bool all = false)
    {
        IEnumerable<PropertyRow> Walk(PropertyRow row)
        {
            yield return row;
            if (all || row.IsExpanded)
                foreach (var child in row.Children.SelectMany(Walk)) yield return child;
        }
        return grid.Roots.SelectMany(Walk).ToArray();
    }

    private static PropertyRow Row(PropertyGridViewModel grid, string id) =>
        Flattened(grid, all: true).FirstOrDefault(row => row.Id == id && row.Kind != PropertyRowKind.TableRow) ?? throw new KeyNotFoundException(id);

    private static (SelectionViewModel, PropertyGridViewModel) Model()
    {
        var selection = new SelectionViewModel(new PropertyEditorViewModel());
        return (selection, new PropertyGridViewModel(selection, CultureInfo.InvariantCulture));
    }

    private static PropertyGridViewModel Grid(params EntityDataSnapshot[] snapshots) => Grid(File.ReadAllText(ExamplePath), snapshots);

    private static PropertyGridViewModel Grid(string json, params EntityDataSnapshot[] snapshots) => Grid(json, false, snapshots);

    private static PropertyGridViewModel Grid(string json, bool allowErrors, params EntityDataSnapshot[] snapshots)
    {
        var (selection, grid) = Model();
        selection.Apply(snapshots, cache: Cache(json, allowErrors));
        return grid;
    }

    private static ObjectValuesCache Cache(string? json = null, bool allowErrors = false)
    {
        var result = ParameterSchemaJson.Load(json ?? File.ReadAllText(ExamplePath));
        Assert.NotNull(result.Schema);
        Assert.True(allowErrors || !result.HasErrors, string.Join("\n", result.Issues.Select(issue => issue.Message)));
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }

    private static EntityDataSnapshot Device(string handle, params DataValue[][] pairs) => Object(handle, "DEVICE", pairs);

    private static EntityDataSnapshot Object(string handle, string? template, params DataValue[][] pairs)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2) };
        if (template != null) xdata.AddRange(Pair(XDataV2.TemplateKey, template));
        foreach (var pair in pairs) xdata.AddRange(pair);
        return new EntityDataSnapshot(handle, "BlockReference", xdata.ToArray(), Array.Empty<DataRecord>());
    }

    private static EntityDataSnapshot Raw(string handle, string entityType, params DataValue[] xdata) => new EntityDataSnapshot(handle, entityType, xdata, Array.Empty<DataRecord>());

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

    private static DataValue[] Pair(string id, DataValue[] list) =>
        new[] { V(1000, id) }.Concat(list.Length == 0 ? new[] { V(1002, "{"), V(1002, "}") } : new[] { V(1002, "{") }.Concat(list).Concat(new[] { V(1002, "}") })).ToArray();

    private static DataValue[] Materials(params (string Category, string Mark, double Count, bool InSpec, string Comment)[] rows)
    {
        var list = new List<DataValue>();
        foreach (var row in rows)
        {
            list.Add(V(1002, "{"));
            if (row.Category.Length != 0) list.AddRange(Pair("Mat_Category", row.Category));
            list.AddRange(Pair("Mat_Mark", row.Mark));
            list.AddRange(Pair("Mat_Count", row.Count));
            list.AddRange(Pair("Mat_InSpec", (short)(row.InSpec ? 1 : 0)));
            if (row.Comment.Length != 0) list.AddRange(Pair("Mat_Comment", row.Comment));
            list.Add(V(1002, "}"));
        }
        return Pair("Materials", list.ToArray());
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);

    private sealed class Source : ISelectionSource, IObjectValuesSource
    {
        public IReadOnlyList<EntityDataSnapshot> Snapshot { get; set; } = Array.Empty<EntityDataSnapshot>();
        public int Reads { get; private set; }
        public ObjectValuesCache? Values { get; } = Cache();
        public event EventHandler? SelectionChanged { add { } remove { } }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() { Reads++; return Snapshot; }
    }
}
