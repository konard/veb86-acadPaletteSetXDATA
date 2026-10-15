using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>Acceptance of TZXDATA stage 3: XDATA v2 (section 6), its limits and the value cache (10.2).</summary>
public sealed class XDataV2Tests
{
    private const string Schema = @"{ 'format': 'BobrovXDATA.schema', 'version': 1,
        'parameters': [
            { 'id': 'S', 'type': 'String', 'caption': 'Строка' },
            { 'id': 'D', 'type': 'Double', 'caption': 'Число', 'editor': { 'min': 0, 'max': 100 } },
            { 'id': 'I', 'type': 'Integer', 'caption': 'Целое' },
            { 'id': 'B', 'type': 'Boolean', 'caption': 'Флаг' },
            { 'id': 'E', 'type': 'Enum', 'caption': 'Список', 'enum': 'L' },
            { 'id': 'C', 'type': 'CalculatedString', 'caption': 'Формула', 'format': '@@[S]' },
            { 'id': 'Name', 'type': 'String', 'caption': 'Материал' },
            { 'id': 'Count', 'type': 'Double', 'caption': 'Количество' },
            { 'id': 'InSpec', 'type': 'Boolean', 'caption': 'В спецификации' },
            { 'id': 'Materials', 'type': 'Table', 'caption': 'Материалы', 'columns': ['Name', 'Count', 'InSpec'], 'key': 'Name' },
            { 'id': 'M', 'type': 'Money', 'caption': 'Сумма' } ],
        'enums': [ { 'id': 'L', 'caption': 'Список', 'items': [ { 'key': 'a', 'caption': 'А' }, { 'key': 'b', 'caption': 'Б' } ] } ],
        'templates': [
            { 'id': 'X', 'caption': 'Шаблон X', 'parameters': [ 'S', 'D' ] },
            { 'id': 'Y', 'caption': 'Шаблон Y', 'parameters': [ 'I' ] } ] }";

    private static readonly Guid Guid1 = new Guid("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Guid2 = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void AllTypesRoundTripWithTheirGroupCodes()
    {
        var snapshot = Patch(Object("A"),
            SelectionEdit.SetParameter("S", "Текст; с =знаками"),
            SelectionEdit.SetParameter("D", "2,5"),
            SelectionEdit.SetParameter("I", 3),
            SelectionEdit.SetParameter("B", true),
            SelectionEdit.SetParameter("E", "b"),
            SelectionEdit.AddTableRow("Materials", Cells(("Name", "Кабель"), ("Count", 12.5), ("InSpec", false))),
            SelectionEdit.AddTableRow("Materials", Cells(("Name", "Лоток"), ("Count", 3), ("InSpec", true))));

        Assert.Equal(new object[]
        {
            V(1001, "BobrovXDATA"), V(1070, (short)2),
            V(1000, "S"), V(1000, "Текст; с =знаками"),
            V(1000, "D"), V(1040, 2.5),
            V(1000, "I"), V(1071, 3),
            V(1000, "B"), V(1070, (short)1),
            V(1000, "E"), V(1000, "b"),
            V(1000, "Materials"), V(1002, "{"),
                V(1002, "{"), V(1000, "Name"), V(1000, "Кабель"), V(1000, "Count"), V(1040, 12.5), V(1000, "InSpec"), V(1070, (short)0), V(1002, "}"),
                V(1002, "{"), V(1000, "Name"), V(1000, "Лоток"), V(1000, "Count"), V(1040, 3.0), V(1000, "InSpec"), V(1070, (short)1), V(1002, "}"),
            V(1002, "}")
        }.Select(Text), snapshot.XData.Select(Text));

        var values = XDataV2.Read(snapshot, Snapshot());
        Assert.Equal(XDataFormat.V2, values.Format);
        Assert.Empty(values.Warnings);
        Assert.All(values.Values, value => Assert.Equal(ObjectValueState.Typed, value.State));
        Assert.Equal("Текст; с =знаками", values.Find("s")!.Value);
        Assert.Equal(2.5, values.Find("D")!.Value);
        Assert.Equal(3, values.Find("I")!.Value);
        Assert.Equal(true, values.Find("B")!.Value);
        Assert.Equal("b", values.Find("E")!.Value);
        var rows = values.Find("Materials")!.Rows!;
        Assert.Equal(new[] { "Кабель", "Лоток" }, rows.Select(row => row.Key));
        Assert.Equal(12.5, rows[0].Find("Count")!.Value);
        Assert.Equal(false, rows[0].Find("InSpec")!.Value);
        Assert.Equal(3.0, rows[1].Find("Count")!.Value);

        // Writing the values that were read gives the same XDATA.
        var again = values.Values.Where(value => !value.IsTable)
            .Aggregate(snapshot, (current, value) => Patcher().Apply(current, SelectionEdit.SetParameter(value.Id, value.Value)));
        Assert.Equal(snapshot.XData.Select(Text), again.XData.Select(Text));
    }

    [Fact]
    public void ValuesOutsideTheSchemaAreShownAsStringsAndKept()
    {
        var snapshot = V2("A",
            V(1000, "Old"), V(1040, 1.25),
            V(1000, "D"), V(1000, "много"),
            V(1000, "I"), V(1040, 3.0),
            V(1000, "B"), V(1070, (short)5),
            V(1000, "E"), V(1000, "zzz"),
            V(1000, "M"), V(1040, 10.0),
            V(1000, "Rows"), V(1002, "{"), V(1002, "{"), V(1000, "X"), V(1071, 1), V(1002, "}"), V(1002, "}"),
            V(1071, 77));
        var values = XDataV2.Read(snapshot, Snapshot());

        Assert.Equal(ObjectValueState.NotInSchema, values.Find("Old")!.State);
        Assert.Equal("1.25", values.Find("Old")!.Text);
        Assert.Equal(ObjectValueState.TypeMismatch, values.Find("D")!.State);
        Assert.Equal("много", values.Find("D")!.Value);
        Assert.Equal(ObjectValueState.TypeMismatch, values.Find("I")!.State);
        Assert.Equal(ObjectValueState.TypeMismatch, values.Find("B")!.State);
        Assert.Equal(ObjectValueState.UnknownEnumKey, values.Find("E")!.State);
        Assert.Equal("zzz", values.Find("E")!.Value);
        Assert.Equal(ObjectValueState.UnknownType, values.Find("M")!.State);
        Assert.Equal(ObjectValueState.NotInSchema, values.Find("Rows")!.State);
        Assert.Equal("X", values.Find("Rows")!.Rows!.Single().Cells.Single().Id);
        Assert.Contains("Old: не описан в схеме", values.Warnings);
        Assert.Contains("Значение XDATA без Id пропущено.", values.Warnings);

        // Changing one value keeps every value outside the schema and the value without an Id.
        var after = Patcher().Apply(snapshot, SelectionEdit.SetParameter("S", "новое"));
        Assert.Equal(snapshot.XData.Select(Text).Concat(new[] { Text(V(1000, "S")), Text(V(1000, "новое")) }), after.XData.Select(Text));
        // A value outside the schema can be removed (TZXDATA 6.5), but not changed.
        var removed = Patcher().Apply(snapshot, SelectionEdit.RemoveParameter("old"));
        Assert.Null(XDataV2.Read(removed, Snapshot()).Find("Old"));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(snapshot, SelectionEdit.SetParameter("Old", "1")));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(snapshot, SelectionEdit.SetParameter("M", 1.0)));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(snapshot, SelectionEdit.SetParameter("C", "x")));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(snapshot, SelectionEdit.SetParameter("E", "zzz")));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(snapshot, SelectionEdit.SetParameter("D", 101.0)));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(snapshot, SelectionEdit.SetParameter("I", 1.5)));
    }

    [Fact]
    public void DamagedOrForeignDataIsReadWithWarningsAndNotWritten()
    {
        var newer = new EntityDataSnapshot("A", "Line", new[] { V(1001, "BobrovXDATA"), V(1070, (short)3), V(1000, "S"), V(1000, "x") }, Array.Empty<DataRecord>());
        Assert.Equal(XDataFormat.Newer, XDataV2.Read(newer, Snapshot()).Format);
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(newer, SelectionEdit.SetParameter("S", "y")));

        var damaged = V2("A", V(1000, "Materials"), V(1002, "{"), V(1002, "{"), V(1000, "Name"), V(1000, "x"));
        Assert.Contains("Незакрытый или лишний список XDATA BobrovXDATA.", XDataV2.Read(damaged, Snapshot()).Warnings);
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(damaged, SelectionEdit.SetParameter("S", "y")));

        var repeated = V2("A", V(1000, "S"), V(1000, "first"), V(1000, "s"), V(1000, "second"));
        Assert.Equal("first", XDataV2.Read(repeated, Snapshot()).Find("S")!.Value);
        var fixedUp = Patcher().Apply(repeated, SelectionEdit.SetParameter("S", "third"));
        Assert.Equal(new[] { "BobrovXDATA", "2", "S", "third" }, fixedUp.XData.Select(value => XDataV2.FormatValue(value.Value)));

        var v1 = Object("A", V(1001, "BobrovXDATA"), V(1000, "Name=Old"));
        Assert.Equal(XDataFormat.V1, XDataV2.FormatOf(v1.XData));
        Assert.Throws<InvalidOperationException>(() => Patcher().Apply(v1, SelectionEdit.SetParameter("S", "y")));
        var v2 = V2("A", V(1000, "S"), V(1000, "x"));
        Assert.Throws<InvalidOperationException>(() => new XDataPatch().Apply(v2, SelectionEdit.Header("Name", "y")));
        // Without a schema in NOD the v2 values are not written.
        Assert.Throws<InvalidOperationException>(() => new XDataPatch().Apply(v2, SelectionEdit.SetParameter("S", "y")));
    }

    [Fact]
    public void OtherRegAppsAndXRecordsAreKeptInTheirOrder()
    {
        var other = new[] { V(1001, "OtherApp"), V(1000, "keep"), V(1040, 1.5), V(1071, 9) };
        var last = new[] { V(1001, "LastApp"), V(1005, "1F") };
        var record = new DataRecord("Unrelated", new[] { V(90, 42) });
        var before = new EntityDataSnapshot("A", "Line",
            other.Concat(new[] { V(1001, "BobrovXDATA"), V(1070, (short)2), V(1000, "S"), V(1000, "x") }).Concat(last).ToArray(), new[] { record });
        var after = Patcher().Apply(before, SelectionEdit.SetParameter("D", 4.0));

        Assert.Equal(other, after.XData.Take(4));
        Assert.Equal(last, after.XData.Skip(after.XData.Count - 2));
        Assert.Same(before.XData[0], after.XData[0]);
        Assert.Same(record, after.Records.Single());

        var created = Patcher().Apply(Object("B", other), SelectionEdit.SetParameter("S", "y"));
        Assert.Equal(other.Concat(new[] { V(1001, "BobrovXDATA"), V(1070, (short)2), V(1000, "S"), V(1000, "y") }).Select(Text), created.XData.Select(Text));
        var empty = Object("C", V(1001, "BobrovXDATA"), V(1001, "OtherApp"), V(1000, "keep"));
        Assert.Equal(XDataFormat.None, XDataV2.FormatOf(empty.XData));
        Assert.Equal(new[] { "BobrovXDATA", "2", "S", "y", "OtherApp", "keep" },
            Patcher().Apply(empty, SelectionEdit.SetParameter("S", "y")).XData.Select(value => XDataV2.FormatValue(value.Value)));
    }

    [Fact]
    public void StringOver255BytesRefusesTheWholeGroup()
    {
        var database = new MemoryDatabase(V2("A", V(1000, "S"), V(1000, "a")), V2("B"));
        var original = database.Snapshots;
        var ok = new string('Ж', 127) + "z";
        Patcher().Apply(original[0], SelectionEdit.SetParameter("S", ok));
        var error = Assert.Throws<InvalidOperationException>(() => Writer(database)
            .WriteSelection(new[] { "A", "B" }, SelectionEdit.SetParameter("S", new string('Ж', 128))));
        Assert.Contains("255", error.Message);
        Assert.Same(original, database.Snapshots);
        Assert.Equal(0, database.Writes);
    }

    [Fact]
    public void XDataOver16KbRefusesTheWholeGroup()
    {
        // Other RegApps fill the object B close to the limit; the same edit fits object A.
        var filler = Enumerable.Range(0, 64).Select(index => V(1000, new string('w', 250))).ToArray();
        var full = new EntityDataSnapshot("B", "Line", new[] { V(1001, "OtherApp") }.Concat(filler).Concat(new[] { V(1001, "BobrovXDATA"), V(1070, (short)2) }).ToArray(), Array.Empty<DataRecord>());
        Assert.InRange(XDataV2.EstimateSize(full.XData), 16000, XDataV2.MaxXDataBytes);
        var database = new MemoryDatabase(V2("A"), full);
        var original = database.Snapshots;

        var error = Assert.Throws<InvalidOperationException>(() => Writer(database)
            .WriteSelection(new[] { "A", "B" }, SelectionEdit.SetParameter("S", new string('x', 250))));
        Assert.Contains("16383", error.Message);
        Assert.Contains("B", error.Message);
        Assert.Same(original, database.Snapshots);
        Assert.Equal(0, database.Writes);

        Writer(database).WriteSelection(new[] { "A", "B" }, SelectionEdit.SetParameter("S", "x"));
        Assert.Equal(1, database.Commits);
        Assert.Contains("16383", XDataV2.Read(new EntityDataSnapshot("C", "Line", filler.Concat(filler).ToArray(), Array.Empty<DataRecord>()), Snapshot()).Warnings.Single());
    }

    [Fact]
    public void GroupWriteOfOneFieldKeepsEveryOtherValue()
    {
        var a = V2("A", V(1000, "S"), V(1000, "a"), V(1000, "D"), V(1040, 1.0));
        var b = V2("B", V(1000, "I"), V(1071, 7), V(1000, "Old"), V(1000, "keep"), V(1000, "D"), V(1040, 2.0), V(1000, "B"), V(1070, (short)1));
        var c = V2("C");
        var database = new MemoryDatabase(a, b, c);
        var cache = Cache();
        Writer(database, cache).WriteSelection(new[] { "A", "B", "C" }, SelectionEdit.SetParameter("D", 5.5));

        Assert.Equal(1, database.Commits);
        Assert.Equal(3, database.Writes);
        Assert.Equal(new[] { "S", "a", "D", "5.5" }, Payload(database.Snapshots[0]));
        Assert.Equal(new[] { "I", "7", "Old", "keep", "D", "5.5", "B", "1" }, Payload(database.Snapshots[1]));
        Assert.Equal(new[] { "D", "5.5" }, Payload(database.Snapshots[2]));
    }

    [Fact]
    public void TableRowsAreEditedOneRowAtATime()
    {
        var start = Patch(V2("A", V(1000, "S"), V(1000, "a")),
            SelectionEdit.AddTableRow("Materials", Cells(("Name", "Кабель"), ("Count", 1.0))),
            SelectionEdit.AddTableRow("Materials", Cells(("Name", "Лоток"), ("Count", 2.0), ("InSpec", true))));
        var edited = Patch(start, SelectionEdit.SetTableCell("Materials", "Кабель", "Count", "4,5"));
        var rows = XDataV2.Read(edited, Snapshot()).Find("Materials")!.Rows!;
        Assert.Equal(4.5, rows[0].Find("Count")!.Value);
        Assert.Equal(2.0, rows[1].Find("Count")!.Value);
        Assert.Equal(true, rows[1].Find("InSpec")!.Value);
        Assert.Equal("a", XDataV2.Read(edited, Snapshot()).Find("S")!.Value);

        // A row missing on an object of the group is added with its key.
        var added = XDataV2.Read(Patch(edited, SelectionEdit.SetTableCell("Materials", "Труба", "InSpec", false)), Snapshot()).Find("Materials")!.Rows!;
        Assert.Equal(new[] { "Кабель", "Лоток", "Труба" }, added.Select(row => row.Key));
        Assert.Equal(new[] { "Name", "InSpec" }, added[2].Cells.Select(cell => cell.Id));

        var deleted = XDataV2.Read(Patch(edited, SelectionEdit.DeleteTableRow("Materials", "Кабель")), Snapshot()).Find("Materials")!.Rows!;
        Assert.Equal("Лоток", deleted.Single().Key);

        Assert.Throws<InvalidOperationException>(() => Patch(edited, SelectionEdit.AddTableRow("Materials", Cells(("Name", "Лоток")))));
        Assert.Throws<ArgumentException>(() => Patch(edited, SelectionEdit.AddTableRow("Materials", Cells(("Count", 1.0)))));
        Assert.Throws<InvalidOperationException>(() => Patch(edited, SelectionEdit.SetTableCell("Materials", "Лоток", "S", "x")));
        Assert.Throws<InvalidOperationException>(() => Patch(edited, SelectionEdit.SetParameter("Materials", "x")));
        Assert.Throws<InvalidOperationException>(() => Patch(edited, SelectionEdit.SetTableCell("S", "x", "Name", "x")));
    }

    [Fact]
    public void TemplateKeepsValuesAndCreatesDeviceId()
    {
        var guids = new Queue<Guid>(new[] { Guid1, Guid2 });
        var patch = new XDataPatch(Snapshot(), newGuid: guids.Dequeue);
        var start = V2("A", V(1000, "S"), V(1000, "a"), V(1000, "I"), V(1071, 2));
        var withTemplate = patch.Apply(start, SelectionEdit.SetTemplate("x"));
        Assert.Equal(new[] { "DEVICE_ID", "11111111-2222-3333-4444-555555555555", "TEMPLATE", "X", "S", "a", "I", "2" }, Payload(withTemplate));

        var changed = patch.Apply(withTemplate, SelectionEdit.SetTemplate("Y"));
        var values = XDataV2.Read(changed, Snapshot());
        Assert.Equal("Y", values.Template);
        Assert.Equal("11111111-2222-3333-4444-555555555555", values.DeviceId);
        Assert.Equal(new[] { "S", "I" }, values.Values.Select(value => value.Id));
        Assert.Empty(values.Warnings);

        var renewed = XDataV2.Read(patch.Apply(changed, SelectionEdit.NewDeviceId()), Snapshot());
        Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", renewed.DeviceId);
        Assert.Null(XDataV2.Read(patch.Apply(changed, SelectionEdit.SetTemplate("")), Snapshot()).Template);
        Assert.Throws<InvalidOperationException>(() => patch.Apply(changed, SelectionEdit.SetTemplate("Z")));
        Assert.Throws<ArgumentException>(() => patch.Apply(changed, SelectionEdit.SetParameter("DEVICE_ID", "x")));
        Assert.Throws<ArgumentException>(() => patch.Apply(changed, SelectionEdit.RemoveParameter("template")));

        var unknown = V2("B", V(1000, "TEMPLATE"), V(1000, "Q"), V(1000, "DEVICE_ID"), V(1000, "not a guid"));
        Assert.Equal(2, XDataV2.Read(unknown, Snapshot()).Warnings.Count);
    }

    [Fact]
    public void DuplicateDeviceIdKeepsTheSmallestHandle()
    {
        var id = XDataV2.NewDeviceId(Guid1);
        var objects = new[] { "1F", "A", "2", "3" }.Select(handle =>
            XDataV2.Read(V2(handle, V(1000, "DEVICE_ID"), V(1000, handle == "3" ? XDataV2.NewDeviceId(Guid2) : handle == "A" ? id.ToLowerInvariant() : id)), Snapshot())).ToArray();
        var duplicate = XDataV2.FindDuplicateDeviceIds(objects).Single();
        Assert.Equal("2", duplicate.Original);
        Assert.Equal(new[] { "A", "1F" }, duplicate.Copies);

        // Each object of a group gets its own new ID.
        var database = new MemoryDatabase(objects.Select(item => V2(item.Handle, V(1000, "DEVICE_ID"), V(1000, id))).ToArray());
        var guids = new Queue<Guid>(Enumerable.Range(1, 4).Select(index => new Guid(index, 0, 0, new byte[8])));
        new XDataWriter(database.Begin, Cache(), guids.Dequeue).WriteSelection(database.Snapshots.Select(s => s.Handle).ToArray(), SelectionEdit.NewDeviceId());
        Assert.Equal(4, database.Snapshots.Select(s => Payload(s)[1]).Distinct().Count());
    }

    [Fact]
    public void CacheParsesEachObjectOnceAndInvalidatesOnlyWrittenObjects()
    {
        var schema = new SchemaCache(Load);
        var cache = new ObjectValuesCache(schema);
        var database = new MemoryDatabase(V2("A", V(1000, "S"), V(1000, "a")), V2("B", V(1000, "S"), V(1000, "b")), V2("C", V(1000, "S"), V(1000, "a")));
        cache.Get(database.Snapshots);
        Assert.Equal(3, cache.ParseCount);
        // A fresh read of unchanged objects (new lists with equal values) is not parsed again.
        var first = cache.Find("A");
        Assert.Same(first, cache.Get(Copy(database.Snapshots[0])));
        cache.Get(database.Snapshots.Select(Copy));
        Assert.Equal(3, cache.ParseCount);

        // The edit is checked from the cache: A and C already have the value, only B is written and parsed again.
        new XDataWriter(database.Begin, cache).WriteSelection(new[] { "A", "B", "C" }, SelectionEdit.SetParameter("S", "a"));
        Assert.Equal(1, database.Writes);
        Assert.Equal(3, cache.ParseCount);
        Assert.Null(cache.Find("B"));
        Assert.Same(first, cache.Find("A"));
        Assert.Equal("a", cache.Get(database.Snapshots[1]).Find("S")!.Value);
        Assert.Equal(4, cache.ParseCount);

        // A change made outside the palette (or Undo) is noticed by the values.
        cache.Get(Patcher().Apply(database.Snapshots[0], SelectionEdit.SetParameter("S", "external")));
        Assert.Equal(5, cache.ParseCount);
        // A new schema drops the whole cache.
        schema.Invalidate();
        Assert.Equal(0, cache.Count);
        cache.Get(database.Snapshots);
        Assert.Equal(8, cache.ParseCount);
        Assert.Equal(2, schema.LoadCount);
    }

    [Fact]
    public void CacheIsBoundedByLru()
    {
        var cache = new ObjectValuesCache(new SchemaCache(Load), capacity: 2);
        var a = V2("A"); var b = V2("B"); var c = V2("C");
        cache.Get(a); cache.Get(b); cache.Get(a); cache.Get(c);
        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.Find("A"));
        Assert.Null(cache.Find("B"));
        Assert.Equal(ObjectValuesCache.DefaultCapacity, new ObjectValuesCache(new SchemaCache(Load)).Capacity);
    }

    [Fact]
    public void SelectionShowsV2WarningsAndDuplicateIdsWithoutV1Fields()
    {
        var id = XDataV2.NewDeviceId(Guid1);
        var snapshots = new[]
        {
            V2("A", V(1000, "DEVICE_ID"), V(1000, id), V(1000, "S"), V(1000, "Name=x"), V(1000, "Old"), V(1000, "1")),
            V2("B", V(1000, "DEVICE_ID"), V(1000, id))
        };
        Assert.Empty(new XDataParser().Parse(snapshots[0]).Warnings);
        Assert.Empty(new XDataParser().Parse(snapshots[0]).Properties);
        var model = new SelectionViewModel(new PropertyEditorViewModel());
        var cache = Cache();
        model.Apply(snapshots, cache: cache);
        Assert.Equal(2, model.Values.Count);
        Assert.Equal(2, cache.ParseCount);
        model.SelectedEntity = model.Entities[0];
        Assert.Contains("Old: не описан в схеме", model.Diagnostics);
        Assert.Contains("повторяется у объектов B", model.Diagnostics);
        model.SelectedEntity = model.Entities[1];
        Assert.Contains("совпадает с объектом A", model.Diagnostics);
        // The next selection event with unchanged objects does not parse again.
        model.Apply(snapshots.Select(Copy).ToArray(), cache: cache);
        Assert.Equal(2, cache.ParseCount);
    }

    [Fact]
    public void ValuesAreConvertedFromInvariantText()
    {
        var types = ParameterTypeRegistry.Default;
        Assert.Null(types.Find("Double")!.Convert(" 1e3 ", out var real));
        Assert.Equal(1000.0, real);
        Assert.NotNull(types.Find("Double")!.Convert(double.NaN, out _));
        Assert.Null(types.Find("Integer")!.Convert(4.0, out var integer));
        Assert.Equal(4, integer);
        Assert.NotNull(types.Find("Integer")!.Convert(3000000000L, out _));
        Assert.Null(types.Find("Boolean")!.Convert("Да", out var flag));
        Assert.Equal(true, flag);
        Assert.NotNull(types.Find("Boolean")!.Convert(2, out _));
        Assert.Null(types.Find("String")!.Convert(1.5, out var text));
        Assert.Equal("1.5", text);
        Assert.False(types.Find("Integer")!.TryRead(V(1070, (short)3), out _));
        Assert.False(types.Find("Double")!.TryRead(V(1040, double.PositiveInfinity), out _));
        Assert.Equal(0, types.Find("Table")!.XDataCode);
    }

    private static SchemaNodReadResult Load()
    {
        var result = ParameterSchemaJson.Load(ParameterSchemaJsonTests.Json(Schema));
        Assert.NotNull(result.Schema);
        return new SchemaNodReadResult(result.Schema, null, result.Issues);
    }

    private static SchemaSnapshot Snapshot() => new SchemaSnapshot(Load());
    private static ObjectValuesCache Cache() => new ObjectValuesCache(new SchemaCache(Load));
    private static XDataPatch Patcher() => new XDataPatch(Snapshot(), newGuid: () => Guid1);
    private static XDataWriter Writer(MemoryDatabase database, ObjectValuesCache? cache = null) => new XDataWriter(database.Begin, cache ?? Cache(), () => Guid1);

    private static EntityDataSnapshot Patch(EntityDataSnapshot snapshot, params SelectionEdit[] edits)
    {
        var patch = Patcher();
        return edits.Aggregate(snapshot, (current, edit) => patch.Apply(current, edit));
    }

    private static IReadOnlyList<KeyValuePair<string, object>> Cells(params (string Id, object Value)[] cells) =>
        cells.Select(cell => new KeyValuePair<string, object>(cell.Id, cell.Value)).ToArray();

    private static EntityDataSnapshot Object(string handle, params DataValue[] xdata) => new EntityDataSnapshot(handle, "Line", xdata, Array.Empty<DataRecord>());
    private static EntityDataSnapshot V2(string handle, params DataValue[] pairs) =>
        Object(handle, new[] { V(1001, "BobrovXDATA"), V(1070, (short)2) }.Concat(pairs).ToArray());
    private static EntityDataSnapshot Copy(EntityDataSnapshot snapshot) =>
        new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, snapshot.XData.Select(value => new DataValue(value.TypeCode, value.Value)).ToArray(), snapshot.Records);

    /// <summary>The v2 pairs after <c>(1070 . 2)</c> as text.</summary>
    private static string[] Payload(EntityDataSnapshot snapshot) => snapshot.XData.Skip(2).Select(value => XDataV2.FormatValue(value.Value)).ToArray();
    private static string Text(DataValue value) => value.TypeCode + ":" + value.Value.GetType().Name + ":" + XDataV2.FormatValue(value.Value);
    private static string Text(object value) => Text((DataValue)value);
    private static DataValue V(int code, object value) => new DataValue(code, value);

    private sealed class MemoryDatabase
    {
        public MemoryDatabase(params EntityDataSnapshot[] snapshots) => Snapshots = snapshots;
        public EntityDataSnapshot[] Snapshots { get; private set; }
        public int Writes { get; private set; }
        public int Commits { get; private set; }
        public IXDataWriteTransaction Begin(IReadOnlyList<string> handles) => new Transaction(this, handles);

        private sealed class Transaction : IXDataWriteTransaction
        {
            private readonly MemoryDatabase database;
            private readonly EntityDataSnapshot[] staged;
            public Transaction(MemoryDatabase database, IReadOnlyList<string> handles)
            {
                this.database = database;
                staged = database.Snapshots.ToArray();
                Snapshots = handles.Select(handle => database.Snapshots.Single(s => s.Handle == handle)).ToArray();
            }
            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }
            public void Write(EntityDataSnapshot snapshot)
            {
                database.Writes++;
                staged[Array.FindIndex(staged, s => s.Handle == snapshot.Handle)] = snapshot;
            }
            public void Commit() { database.Snapshots = staged; database.Commits++; }
            public void Dispose() { }
        }
    }
}
