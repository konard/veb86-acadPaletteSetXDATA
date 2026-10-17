using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;

// Prints the reports of VELEC_MIGRATE and VELEC_CHECK (TZXDATA stage 9) for the objects of examples/stage3-selection.lsp
// and examples/tzxdata-stage9-migrate.lsp, without AutoCAD. Usage: MigrationReport [--remove] <schema.json>
// Exit code 1 when the schema has errors, the legacy VST changes after the migration or a repeated migration changes anything.
var remove = args.Contains("--remove");
var path = args.LastOrDefault(argument => argument != "--remove") ?? "examples/schema.json";
var load = ParameterSchemaJson.LoadFile(path);
if (load.Schema is null || load.HasErrors)
{
    Console.WriteLine(path + ": ошибки схемы");
    foreach (var issue in load.Issues) Console.WriteLine("  " + issue);
    return 1;
}
var schema = new SchemaSnapshot(new SchemaNodReadResult(load.Schema, null, load.Issues));
var next = 0;
Func<Guid> guids = () => new Guid(++next, 0x1111, 0x4222, 0x83, 0x33, 0, 0, 0, 0, 0, 0);
const string SharedId = "{0B1C2D3E-0000-4000-8000-000000000001}";

var drawing = new[]
{
    // examples/stage3-selection.lsp: ESMT strings of the LINE, v1 strings and the XML XRecord of the LWPOLYLINE.
    new EntityDataSnapshot("1F0", "Line", new[]
    {
        V(1001, DrawingNames.EsmtRegApp), V(1000, "Type=Superline"), V(1000, "Number=021"), V(1000, "Name=Line A"),
        V(1000, "ProjectReference=Project 021"), V(1000, "Material"), V(1002, "{"), V(1000, "Category=Steel"), V(1000, "Name=CD35"),
        V(1000, "Count=1,2"), V(1000, "IsInSpec=1"), V(1000, "Comment=Example"), V(1002, "}")
    }, Array.Empty<DataRecord>()),
    new EntityDataSnapshot("1F1", "Polyline", new[] { V(1001, DrawingNames.RegApp), V(1000, "Number=022") }, new[]
    {
        new DataRecord(DrawingNames.RegApp, new[]
        {
            V(1, "<VisualTreeString><Properties><Type>Cable</Type><Name>Cable B</Name></Properties>"),
            V(1, "<Materials><Material Category='Armature' Name='CD35' Count='2' IsInSpec='false'/></Materials></VisualTreeString>")
        })
    }),
    // examples/tzxdata-stage9-migrate.lsp: the earlier RegApp SMARTLINE, two copies of one DEVICE_ID and data outside the schema.
    new EntityDataSnapshot("200", "Line", new[] { V(1001, DrawingNames.LegacyRegApp), V(1000, "Тип=Щит"), V(1000, "Название=ЩР-1"), V(1000, "Номер=7"), V(1000, "Материал"), V(1002, "{"), V(1000, "Категория=Кабель"), V(1000, "Марка=ВВГ 3x2,5"), V(1000, "Количество=12"), V(1002, "}") },
        Array.Empty<DataRecord>()),
    Device("21A", ("DEVICE_ID", SharedId), ("NMO_Name", "ЩР-2")),
    Device("9F", ("DEVICE_ID", SharedId), ("NMO_Name", "ЩР-2")),
    Device("230", ("DEVICE_ID", "{0B1C2D3E-0000-4000-8000-000000000002}"), ("Old_Field", "x"), ("Device_Type", "Щит"))
};

var failed = false;
Console.WriteLine("== VELEC_MIGRATE (" + (remove ? "Удалить" : "Сохранить") + ") ==");
var migration = new SchemaMigration(schema, remove, newGuid: guids);
var results = drawing.Select(migration.Migrate).ToArray();
Print(SchemaMigration.Report(results, remove));
var migrated = results.Select(result => result.After).ToArray();

Console.WriteLine();
Console.WriteLine("== VST прежнего формата (stage3-selection.lsp) ==");
foreach (var (before, after) in drawing.Zip(migrated, (before, after) => (before, after)).Take(2))
{
    var legacy = new XDataParser().Parse(before);
    var values = XDataV2.Read(after, schema);
    var same = legacy.Properties.All(property => migration.Map.Field(property.Key, schema) is string id && values.Find(id)?.Text == property.Value)
        && legacy.Materials.Count == (values.Find("Materials")?.Rows?.Count ?? 0);
    Console.WriteLine(before.Handle + ": полей " + legacy.Properties.Count + ", материалов " + legacy.Materials.Count + " — " + (same ? "те же значения в v2" : "ЗНАЧЕНИЯ РАЗЛИЧАЮТСЯ"));
    failed |= !same;
}

Console.WriteLine();
Console.WriteLine("== VELEC_MIGRATE повторно ==");
var again = migrated.Select(new SchemaMigration(schema, remove, newGuid: guids).Migrate).ToArray();
Print(SchemaMigration.Report(again, remove));
var changed = again.Where(result => result.Changed).Select(result => result.Handle).ToArray();
Console.WriteLine(changed.Length == 0 ? "Повторный перенос ничего не меняет." : "ПОВТОРНЫЙ ПЕРЕНОС МЕНЯЕТ: " + string.Join(", ", changed));
failed |= changed.Length != 0;

Console.WriteLine();
Console.WriteLine("== VELEC_CHECK ==");
var check = new DrawingCheck(schema, newGuid: guids);
var report = check.Run(migrated);
Print(report.Lines());
var fixedDrawing = migrated.Select(snapshot => check.Fix(snapshot, report, DrawingFix.All)).ToArray();
Console.WriteLine();
Console.WriteLine("== VELEC_CHECK после «Исправить: Все» ==");
Print(check.Run(fixedDrawing).Lines());
return failed ? 1 : 0;

static void Print(IEnumerable<string> lines) { foreach (var line in lines) Console.WriteLine(line); }

static DataValue V(int code, object value) => new DataValue(code, value);

static EntityDataSnapshot Device(string handle, params (string Id, string Value)[] pairs)
{
    var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2), V(1000, XDataV2.TemplateKey), V(1000, "DEVICE") };
    foreach (var (id, value) in pairs) { xdata.Add(V(1000, id)); xdata.Add(V(1000, value)); }
    return new EntityDataSnapshot(handle, "BlockReference", xdata, Array.Empty<DataRecord>());
}
