using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #61, option B: a <c>Repeat</c> parameter describes records <c>&lt;id&gt;_&lt;n&gt;_&lt;суффикс&gt;</c> whose count is
/// unknown in advance. The object stores plain pairs; the schema is examples/issue61-repeat-schema.json.
/// </summary>
public sealed class RepeatItemsTests
{
    internal static readonly string RepeatSchemaPath = Path.Combine(AppContext.BaseDirectory, "examples", "issue61-repeat-schema.json");

    internal static ObjectValuesCache RepeatCache()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(RepeatSchemaPath));
        Assert.NotNull(result.Schema);
        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues));
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }

    internal static DataValue[][] Spec(int number, string pos, string name, double count) => new[]
    {
        Pair("spec_" + number + "_pos", pos), Pair("spec_" + number + "_Name", name), Pair("spec_" + number + "_col", count),
    };

    private static EntityDataSnapshot Device(string handle, params DataValue[][] pairs) => Object(handle, "DEVICE", pairs);

    private static IReadOnlyList<string> Ids(EntityDataSnapshot snapshot, ObjectValuesCache cache) =>
        XDataV2.Read(snapshot, cache.Schema).Values.Select(value => value.Id).ToArray();

    [Fact]
    public void RecordFieldsAreTypedByTheFieldParameterCaseInsensitively()
    {
        var cache = RepeatCache();
        var values = XDataV2.Read(Device("A", Spec(1, "1", "Шкаф", 2).Concat(new[] { Pair("SPEC_2_name", "Кабель") }).ToArray()), cache.Schema);

        Assert.Equal(2.0, values.Find("spec_1_col")!.Value);
        Assert.Equal("Кол.", values.Find("spec_1_col")!.Parameter!.Caption);
        Assert.Equal("Спецификация №1", values.Find("spec_1_col")!.Parameter!.Group);
        Assert.Equal(ObjectValueState.Typed, values.Find("spec_1_col")!.State);
        // The stored spelling is kept; the lookup is case-insensitive.
        Assert.Equal("SPEC_2_name", values.Find("spec_2_Name")!.Id);
        Assert.Equal("Спецификация №2", values.Find("spec_2_Name")!.Parameter!.Group);
        Assert.Equal(new[] { 1, 2 }, RepeatItems.Numbers(values, cache.Schema.Parameters.FindRepeat("SPEC")!));

        // Enumeration of the schema returns only declared parameters; leading zeros and unknown suffixes are not records.
        Assert.DoesNotContain(cache.Schema.Parameters.Keys, key => key.StartsWith("spec_1", StringComparison.OrdinalIgnoreCase));
        Assert.False(cache.Schema.Parameters.ContainsKey("spec_01_pos"));
        Assert.False(cache.Schema.Parameters.ContainsKey("spec_1_unknown"));
        Assert.False(cache.Schema.Parameters.ContainsKey("spec_12345_pos"));
        Assert.True(cache.Schema.Parameters.ContainsKey("spec_1234_pos"));
        Assert.True(cache.Schema.Parameters.IsDeclared("Spec_Pos"));
        Assert.False(cache.Schema.Parameters.IsDeclared("spec_1_pos"));
    }

    [Fact]
    public void AddItemTakesTheNextNumberOfEveryObjectAndFillsTheDefaults()
    {
        var cache = RepeatCache();
        var drawing = new Drawing(cache, Device("A", Spec(1, "1", "Шкаф", 1).Concat(Spec(2, "2", "Кабель", 10)).ToArray()), Device("B"));

        drawing.WriteSelection(new[] { "A", "B" }, SelectionEdit.AddItem("SPEC"));
        Assert.Equal(new object?[] { "", null }, drawing.Stored("spec_3_pos"));
        Assert.Equal(new object?[] { "1", "" }, drawing.Stored("spec_1_pos"));
        // The default of Spec_Count is 1; a string field without a default is empty.
        Assert.Equal(1.0, drawing.Stored("spec_3_col")[0]);
        Assert.Equal(1.0, drawing.Stored("spec_1_col")[1]);
        Assert.Equal(new object?[] { null, null }, drawing.Stored("spec_4_pos"));
        // The record goes after the last record of the object, in the field order of the schema and its spelling.
        Assert.Equal(new[] { "spec_3_pos", "spec_3_Name", "spec_3_col" }, Ids(drawing.Snapshots[0], cache).Where(id => id.StartsWith("spec_3", StringComparison.Ordinal)));

        drawing.WriteSelection(new[] { "B" }, SelectionEdit.AddItem("spec", new[] { new KeyValuePair<string, object>("NAME", "Лоток"), new KeyValuePair<string, object>("col", "2.5") }));
        Assert.Equal("Лоток", drawing.Stored("spec_2_Name")[1]);
        Assert.Equal(2.5, drawing.Stored("spec_2_col")[1]);

        // The Persist formula over the first record follows the records.
        Assert.Equal(new object?[] { "1 Шкаф", " " }, drawing.Stored("Spec_First"));
    }

    [Fact]
    public void AddItemChecksTheFieldsAndValues()
    {
        var cache = RepeatCache();
        var patch = new XDataPatch(cache.Schema, culture: CultureInfo.InvariantCulture);
        var device = Device("A");

        Assert.Contains("нет поля «qty»", Assert.Throws<ArgumentException>(() =>
            patch.Apply(device, SelectionEdit.AddItem("spec", new[] { new KeyValuePair<string, object>("qty", 1.0) }))).Message);
        Assert.Contains("Параметр spec_1_col: значение меньше min", Assert.Throws<InvalidOperationException>(() =>
            patch.Apply(device, SelectionEdit.AddItem("spec", new[] { new KeyValuePair<string, object>("col", -1.0) }))).Message);
        Assert.Contains("не является набором записей", Assert.Throws<InvalidOperationException>(() =>
            patch.Apply(device, SelectionEdit.AddItem("Spec_Pos"))).Message);
    }

    [Fact]
    public void AddItemThatDoesNotFitTheXDataLimitIsCancelledWithAMessage()
    {
        var cache = RepeatCache();
        var text = new string('ш', 120); // 240 bytes of UTF-8 in every field.
        // The stored Persist result of the template formula over the first record.
        var pairs = new List<DataValue[]> { Pair("Spec_First", " " + text) };
        var number = 1;
        while (XDataV2.EstimateSize(Device("A", pairs.ToArray()).XData) < XDataV2.MaxXDataBytes - 500)
            pairs.AddRange(Spec(number++, "", text, 1));
        var drawing = new Drawing(cache, Device("A", pairs.ToArray()), Device("B"));
        var before = drawing.Snapshots;

        var error = Assert.Throws<InvalidOperationException>(() =>
            drawing.WriteSelection(new[] { "A", "B" }, SelectionEdit.AddItem("spec", new[] { new KeyValuePair<string, object>("pos", text), new KeyValuePair<string, object>("Name", text) })));
        Assert.Contains("Объект A: запись «Спецификация №" + number + "» не помещается в XDATA", error.Message);
        Assert.Contains("из 16383 байт", error.Message);
        // One transaction: B did not get its record either.
        Assert.Same(before, drawing.Snapshots);
        Assert.Equal(new object?[] { "", null }, drawing.Stored("spec_1_pos"));

        // An empty record still fits.
        drawing.WriteSelection(new[] { "A" }, SelectionEdit.AddItem("spec"));
        Assert.Equal("", drawing.Stored("spec_" + number + "_pos")[0]);
    }

    [Fact]
    public void RemoveLastItemRemovesTheLargestNumberOfEveryObject()
    {
        var cache = RepeatCache();
        var drawing = new Drawing(cache,
            Device("A", Spec(1, "1", "Шкаф", 1).Concat(Spec(2, "2", "Кабель", 10)).ToArray()),
            Device("B", Spec(1, "1", "Лоток", 3)),
            Device("C", Pair("Device_Type", "Щит")));
        var c = drawing.Snapshots[2];

        drawing.WriteSelection(new[] { "A", "B", "C" }, SelectionEdit.RemoveLastItem("spec"));
        Assert.Equal(new object?[] { "1", null, null }, drawing.Stored("spec_1_pos"));
        Assert.Equal(new object?[] { null, null, null }, drawing.Stored("spec_2_pos"));
        // An object without records is not written.
        Assert.Same(c, drawing.Snapshots[2]);
    }

    [Fact]
    public void DeleteItemShiftsTheFollowingNumbersAndKeepsTheStoredSpelling()
    {
        var cache = RepeatCache();
        var drawing = new Drawing(cache,
            Device("A", Spec(1, "1", "Шкаф", 1).Concat(new[] { Pair("SPEC_2_pos", "2"), Pair("SPEC_2_name", "Кабель") }).Concat(Spec(3, "3", "Лоток", 4)).ToArray()),
            Device("B", Spec(1, "1", "Щит", 1)));
        var b = drawing.Snapshots[1];

        drawing.WriteSelection(new[] { "A", "B" }, SelectionEdit.DeleteItem("spec", 2));
        Assert.Equal(new[] { "spec_1_pos", "spec_1_Name", "spec_1_col", "spec_2_pos", "spec_2_Name", "spec_2_col", "Spec_First" }, Ids(drawing.Snapshots[0], cache));
        Assert.Equal("Лоток", drawing.Stored("spec_2_Name")[0]);
        Assert.Equal(4.0, drawing.Stored("spec_2_col")[0]);
        // B has no record 2 and is left as it is.
        Assert.Same(b, drawing.Snapshots[1]);

        drawing.WriteSelection(new[] { "A" }, SelectionEdit.DeleteItem("spec", 1));
        Assert.Equal("Лоток", drawing.Stored("spec_1_Name")[0]);
        Assert.Equal("3 Лоток", drawing.Stored("Spec_First")[0]);

        // Upper case spelling of the object survives renumbering.
        var upper = new Drawing(cache, Device("A", Pair("SPEC_1_POS", "1"), Pair("SPEC_2_POS", "2")));
        upper.WriteSelection(new[] { "A" }, SelectionEdit.DeleteItem("spec", 1));
        Assert.Equal(new[] { "SPEC_1_POS" }, Ids(upper.Snapshots[0], cache).Where(id => !id.Equals("Spec_First", StringComparison.Ordinal)));
    }

    [Fact]
    public void MoveItemSwapsTheNumbersWithTheNeighbour()
    {
        var cache = RepeatCache();
        var drawing = new Drawing(cache, Device("A", Spec(1, "1", "Шкаф", 1).Concat(Spec(2, "2", "Кабель", 10)).ToArray()));

        drawing.WriteSelection(new[] { "A" }, SelectionEdit.MoveItem("spec", 2, -1));
        Assert.Equal(new object?[] { "Кабель" }, drawing.Stored("spec_1_Name"));
        Assert.Equal(new object?[] { "Шкаф" }, drawing.Stored("spec_2_Name"));
        Assert.Equal(new object?[] { 10.0 }, drawing.Stored("spec_1_col"));
        Assert.Equal(new object?[] { "2 Кабель" }, drawing.Stored("Spec_First"));

        Assert.Contains("нет записи «Спецификация №3»", Assert.Throws<InvalidOperationException>(() =>
            drawing.WriteSelection(new[] { "A" }, SelectionEdit.MoveItem("spec", 2, 1))).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionEdit.MoveItem("spec", 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionEdit.DeleteItem("spec", 0));
    }

    [Fact]
    public void GroupEditOfAFieldSkipsObjectsWithoutTheRecord()
    {
        var cache = RepeatCache();
        var drawing = new Drawing(cache,
            Device("A", Spec(1, "1", "Шкаф", 1).Concat(Spec(2, "2", "Кабель", 10)).ToArray()),
            Device("B", Spec(1, "1", "Щит", 1)));
        var b = drawing.Snapshots[1];

        drawing.WriteSelection(new[] { "A", "B" }, SelectionEdit.SetParameter("spec_2_col", "12"));
        Assert.Equal(new object?[] { 12.0, null }, drawing.Stored("spec_2_col"));
        Assert.Same(b, drawing.Snapshots[1]);

        drawing.WriteSelection(new[] { "A", "B" }, SelectionEdit.SetParameter("spec_1_Name", "Шкаф ШР"));
        Assert.Equal(new object?[] { "Шкаф ШР", "Шкаф ШР" }, drawing.Stored("spec_1_Name"));
        Assert.Equal(new object?[] { "1 Шкаф ШР", "1 Шкаф ШР" }, drawing.Stored("Spec_First"));
    }

    [Fact]
    public void ShiftReportListsTheFormulasThatReferToMovedNumbers()
    {
        var schema = RepeatCache().Schema;
        var spec = schema.Parameters.FindRepeat("spec")!;
        var conn = schema.Parameters.FindRepeat("conn")!;

        Assert.Equal(new[] { "Spec_First" }, RepeatItems.Formulas(schema, spec).Select(formula => formula.Id));
        Assert.Empty(RepeatItems.Formulas(schema, spec, 2));
        Assert.Empty(RepeatItems.Formulas(schema, conn));
        var report = RepeatItems.ShiftReport(schema, spec, 1);
        Assert.Equal("Номера записей «Спецификация» изменились; формулы ссылаются на записи по номеру и теперь используют другие записи: " +
            "Spec_First (spec_1_pos, spec_1_Name).", report);
        Assert.Null(RepeatItems.ShiftReport(schema, spec, 2));
        Assert.Null(RepeatItems.ShiftReport(schema, conn, 1));
    }

    [Fact]
    public void CaptionsOfTheRecordsAndTheCollection()
    {
        var spec = new SchemaParameter { Id = "spec", Type = "Repeat", Caption = "Спецификация №{n}" };
        Assert.Equal("Спецификация №3", RepeatItems.Caption(spec, 3));
        Assert.Equal("Спецификация", RepeatItems.CollectionCaption(spec));
        var plain = new SchemaParameter { Id = "conn", Type = "Repeat", Caption = "Соединение", Group = "Соединения" };
        Assert.Equal("Соединение №2", RepeatItems.Caption(plain, 2));
        Assert.Equal("Соединения", RepeatItems.CollectionCaption(plain));
        Assert.Equal("spec_12_Name", RepeatItems.ItemId("spec", 12, "Name"));
    }
}
