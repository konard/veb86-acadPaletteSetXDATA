using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;

// Issue #61: how the variants that need no code look in today's VST, and how «add» / «delete the last» rows are
// composed from the existing operations (SelectionEdit.AddTableRow / DeleteTableRow, RemoveParameter).
// Every fact the options document (docs/repeating-parameters.md) relies on is checked: exit code 1 when one changes.
// Usage: Issue61Probe <examples directory>
var examples = args.Length > 0 ? args[0] : Path.Combine("..", "..", "..", "examples");
var failures = 0;
void Check(bool condition, string fact)
{
    Console.WriteLine((condition ? "  OK   " : "  FAIL ") + fact);
    if (!condition) failures++;
}

static DataValue V(int code, object value) => new DataValue(code, value);
static IEnumerable<DataValue> Pair(string id, object value) => new[]
{
    V(1000, id),
    value switch { string s => V(1000, s), double d => V(1040, d), int i => V(1071, i), _ => throw new ArgumentException() }
};
static IEnumerable<DataValue> Row(params (string Id, object Value)[] cells) =>
    new[] { V(1002, "{") }.Concat(cells.SelectMany(c => Pair(c.Id, c.Value))).Concat(new[] { V(1002, "}") });
static IEnumerable<DataValue> Table(string id, params IEnumerable<DataValue>[] rows) =>
    new[] { V(1000, id), V(1002, "{") }.Concat(rows.SelectMany(r => r)).Concat(new[] { V(1002, "}") });
static EntityDataSnapshot Object(IEnumerable<DataValue> pairs) =>
    new EntityDataSnapshot("2A", "BlockReference",
        new[] { V(1001, DrawingNames.RegApp), V(1070, (short)2) }.Concat(Pair(XDataV2.TemplateKey, "LUMINAIRE")).Concat(pairs).ToArray(),
        Array.Empty<DataRecord>());
static SchemaSnapshot Schema(string path)
{
    var result = ParameterSchemaJson.LoadFile(path);
    if (result.HasErrors) throw new InvalidOperationException(string.Join("\n", result.Issues));
    return new SchemaSnapshot(new SchemaNodReadResult(result.Schema, null, result.Issues));
}
static IReadOnlyList<PropertyRow> Dump(string title, SchemaSnapshot schema, EntityDataSnapshot snapshot)
{
    Console.WriteLine("== " + title + " (XDATA " + XDataV2.EstimateSize(snapshot.XData) + " байт)");
    var selection = new SelectionViewModel(new PropertyEditorViewModel());
    var grid = new PropertyGridViewModel(selection, CultureInfo.InvariantCulture);
    selection.Apply(new[] { snapshot }, cache: new ObjectValuesCache(new SchemaCache(() => schema.Read)));
    void Walk(PropertyRow row) { Console.WriteLine("  " + row); foreach (var child in row.Children) Walk(child); }
    foreach (var root in grid.Roots) Walk(root);
    return grid.Roots;
}
static string[] Captions(PropertyRow? row) => row?.Children.Select(child => child.Caption).ToArray() ?? Array.Empty<string>();
static PropertyRow? Root(IReadOnlyList<PropertyRow> roots, string caption) => roots.FirstOrDefault(row => row.Caption == caption);
static List<int> Keys(SchemaSnapshot schema, EntityDataSnapshot snapshot, string table) =>
    XDataV2.Read(snapshot, schema).Find(table)?.Rows?.Select(r => int.Parse(r.Key!, CultureInfo.InvariantCulture)).ToList() ?? new List<int>();

// Variant A: Table «Specs» / «Connections» with an Integer key «№».
var tableSchema = Schema(Path.Combine(examples, "issue61-table-schema.json"));
var luminaire = Object(Pair("Device_Type", "Светильник")
    .Concat(Table("Specs",
        Row(("Spec_No", 1), ("Spec_Pos", "1"), ("Spec_Name", "Светильник (корпус)"), ("Spec_Count", 1.0)),
        Row(("Spec_No", 2), ("Spec_Pos", "2"), ("Spec_Name", "Лампа"), ("Spec_Count", 2.0)),
        Row(("Spec_No", 3), ("Spec_Pos", "3"), ("Spec_Name", "Клеммная колодка"), ("Spec_Count", 1.0))))
    .Concat(Table("Connections",
        Row(("Conn_No", 1), ("Conn_Target", "ЩО1.1")),
        Row(("Conn_No", 2), ("Conn_Target", "Выключатель")))));
var a = Dump("Вариант A: Table, как сейчас", tableSchema, luminaire);
Check(Captions(Root(a, "Спецификация")).SequenceEqual(new[] { "1", "2", "3" }),
    "A: строка таблицы подписана значением ключа (1, 2, 3), а не «Спецификация №1»");
Check(Root(a, "Спецификация")!.Children.Select(row => row.Value).SequenceEqual(new[] { "Светильник (корпус)", "Лампа", "Клеммная колодка" }),
    "A: в колонке «Значение» строки — вторая колонка таблицы (Spec_Name)");

var patch = new XDataPatch(tableSchema);
// «Добавить соединение»: next key = max + 1, as «+ Добавить» of the VST does.
var next = Keys(tableSchema, luminaire, "Connections").DefaultIfEmpty(0).Max() + 1;
var added = patch.Apply(luminaire, SelectionEdit.AddTableRow("Connections", new[]
{
    new KeyValuePair<string, object>("Conn_No", next), new KeyValuePair<string, object>("Conn_Target", "Розетка")
}));
Check(Keys(tableSchema, added, "Connections").SequenceEqual(new[] { 1, 2, 3 }), "A: добавление = AddTableRow с ключом max + 1 → 1, 2, 3");
// «Удалить последнее соединение»: the row with the largest key.
var last = Keys(tableSchema, added, "Connections").Max();
var removed = patch.Apply(added, SelectionEdit.DeleteTableRow("Connections", last.ToString(CultureInfo.InvariantCulture)));
Check(Keys(tableSchema, removed, "Connections").SequenceEqual(new[] { 1, 2 }), "A: удаление последнего = DeleteTableRow(max) → 1, 2");

// Size: how many specification rows fit into 16 КБ of XDATA.
var rows = 0;
EntityDataSnapshot big;
do
{
    rows++;
    big = Object(Table("Specs", Enumerable.Range(1, rows).Select(n => Row(("Spec_No", n), ("Spec_Pos", n.ToString(CultureInfo.InvariantCulture)),
        ("Spec_Name", "Клеммная колодка 2,5 мм² ЗНИ-2,5"), ("Spec_Count", 1.0))).ToArray()));
} while (XDataV2.EstimateSize(big.XData) <= XDataV2.MaxXDataBytes);
Console.WriteLine("16 КБ XDATA: помещается строк спецификации " + (rows - 1) + " (по 4 ячейки, наименование 32 символа)");
Check(rows - 1 >= 100, "A: в 16 КБ XDATA помещается не меньше 100 строк спецификации");

// Variant C: fixed numbered parameters spec_N_* / conn_N_* with the groups «Спецификация №N».
var numberedSchema = Schema(Path.Combine(examples, "issue61-numbered-schema.json"));
var numbered = Object(Pair("Device_Type", "Светильник")
    .Concat(Pair("spec_1_pos", "1")).Concat(Pair("spec_1_Name", "Светильник (корпус)")).Concat(Pair("spec_1_col", 1.0))
    .Concat(Pair("spec_2_pos", "2")).Concat(Pair("spec_2_Name", "Лампа")).Concat(Pair("spec_2_col", 2.0))
    .Concat(Pair("conn_1_target", "ЩО1.1")).Concat(Pair("conn_2_target", "Выключатель")));
var c = Dump("Вариант C: пронумерованные параметры схемы, как сейчас", numberedSchema, numbered);
Check(Root(c, "Спецификация №1") != null && Root(c, "Спецификация №2") == null && Captions(Root(c, "Дополнительные")).Contains("Наименование"),
    "C с шаблоном: spec_2_* вне шаблона попадают в «Дополнительные», а не в группу «Спецификация №2»");
// «Удалить последнюю спецификацию»: RemoveParameter of every spec_<max>_* pair.
var values = XDataV2.Read(numbered, numberedSchema);
var maxSpec = values.Values.Select(v => System.Text.RegularExpressions.Regex.Match(v.Id, "^spec_(\\d+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Max();
var numberedPatch = new XDataPatch(numberedSchema);
var trimmed = numbered;
foreach (var id in values.Values.Select(v => v.Id).Where(id => id.StartsWith("spec_" + maxSpec + "_", StringComparison.OrdinalIgnoreCase)).ToList())
    trimmed = numberedPatch.Apply(trimmed, SelectionEdit.RemoveParameter(id));
_ = Dump("Вариант C после удаления спецификации №" + maxSpec, numberedSchema, trimmed);
Check(maxSpec == 2 && !XDataV2.Read(trimmed, numberedSchema).Values.Any(v => v.Id.StartsWith("spec_2_", StringComparison.OrdinalIgnoreCase)),
    "C: удаление последней = RemoveParameter каждого spec_<max>_*");

// Variant C without TEMPLATE: only the stored values are shown (TZXDATA 6.6) — in the groups of their parameters?
var free = new EntityDataSnapshot("2B", "BlockReference",
    new[] { V(1001, DrawingNames.RegApp), V(1070, (short)2) }
        .Concat(Pair("spec_1_pos", "1")).Concat(Pair("spec_1_Name", "Светильник (корпус)"))
        .Concat(Pair("spec_2_pos", "2")).Concat(Pair("spec_2_Name", "Лампа")).Concat(Pair("conn_1_target", "ЩО1.1")).ToArray(),
    Array.Empty<DataRecord>());
var cFree = Dump("Вариант C без шаблона", numberedSchema, free);
Check(cFree.Select(row => row.Caption).SequenceEqual(new[] { "Спецификация №1", "Спецификация №2", "Соединение №1" }),
    "C без шаблона: значения показываются в группах своих параметров «Спецификация №N»");
Console.WriteLine(failures == 0 ? "Issue61Probe: все проверки пройдены." : "Issue61Probe: ошибок " + failures + ".");
return failures == 0 ? 0 : 1;
