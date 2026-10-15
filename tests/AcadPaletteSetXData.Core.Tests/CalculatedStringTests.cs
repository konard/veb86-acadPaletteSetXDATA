using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Acceptance of TZXDATA stage 4: <c>CalculatedString</c> (section 7) — formulas parsed once with their dependency graph,
/// cycles and unknown references, substituted values, recalculation of dependents only, without reading XDATA, and Persist.
/// </summary>
public sealed class CalculatedStringTests
{
    private const string Schema = @"{ 'format': 'BobrovXDATA.schema', 'version': 1,
        'parameters': [
            { 'id': 'HD', 'type': 'String', 'caption': 'Головное устройство' },
            { 'id': 'Group', 'type': 'String', 'caption': 'Группа' },
            { 'id': 'Name', 'type': 'CalculatedString', 'caption': 'Обозначение', 'format': '@@[HD].@@[Group]', 'persist': true },
            { 'id': 'Full', 'type': 'CalculatedString', 'caption': 'Полное', 'format': 'Щит @@[name] (@@[Voltage])' },
            { 'id': 'Power', 'type': 'Double', 'caption': 'Мощность', 'default': 0, 'editor': { 'precision': 2 } },
            { 'id': 'Ratio', 'type': 'Double', 'caption': 'Доля' },
            { 'id': 'Count', 'type': 'Integer', 'caption': 'Количество' },
            { 'id': 'InSpec', 'type': 'Boolean', 'caption': 'В спецификации' },
            { 'id': 'Voltage', 'type': 'Enum', 'caption': 'Напряжение', 'default': '_220', 'enum': 'V' },
            { 'id': 'Note', 'type': 'String', 'caption': 'Примечание', 'default': 'нет' },
            { 'id': 'Spec', 'type': 'CalculatedString', 'caption': 'Строка', 'format': '@@[Power] кВт; @@[Ratio]; @@[Count] шт; @@[InSpec]; @@[Voltage:key]; @@@@ @@[Note]' },
            { 'id': 'Other', 'type': 'String', 'caption': 'Другое' } ],
        'enums': [ { 'id': 'V', 'caption': 'Напряжение', 'items': [ { 'key': '_220', 'caption': '220 В' }, { 'key': '_380', 'caption': '380 В' } ] } ],
        'templates': [
            { 'id': 'DEVICE', 'caption': 'Устройство', 'parameters': [ 'HD', { 'id': 'Group', 'default': '1' }, 'Name', { 'id': 'Note', 'default': 'шаблон' } ] },
            { 'id': 'EMPTY', 'caption': 'Пустой', 'parameters': [ 'Other' ] } ] }";

    /// <summary>A schema that the validator refuses; the drawing may still hold it (NOD edited outside the plugin).</summary>
    private const string Broken = @"{ 'format': 'BobrovXDATA.schema', 'version': 1,
        'parameters': [
            { 'id': 'S', 'type': 'String', 'caption': 'Строка' },
            { 'id': 'A', 'type': 'CalculatedString', 'caption': 'A', 'format': '@@[B]-a', 'persist': true },
            { 'id': 'B', 'type': 'CalculatedString', 'caption': 'B', 'format': '@@[A]-b' },
            { 'id': 'C', 'type': 'CalculatedString', 'caption': 'C', 'format': '@@[A]!' },
            { 'id': 'Self', 'type': 'CalculatedString', 'caption': 'Сам', 'format': '<@@[Self]>' },
            { 'id': 'U', 'type': 'CalculatedString', 'caption': 'U', 'format': '@@[Missing] и @@[S]', 'persist': true },
            { 'id': 'Bad', 'type': 'CalculatedString', 'caption': 'Bad', 'format': '@@[S' } ],
        'templates': [ { 'id': 'T', 'caption': 'T', 'parameters': [ 'S', 'A', 'U' ] } ] }";

    private static readonly CultureInfo Comma = DecimalComma();

    [Fact]
    public void HeadDeviceAndGroupGiveTheDesignation()
    {
        var calculation = Calculate(V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3")));
        Assert.Equal("ЩУ1.3", calculation.Text("Name"));
        Assert.Equal("Щит ЩУ1.3 (220 В)", calculation.Text("Full"));
        Assert.False(calculation.Find("Name")!.IsError);

        // The same with the example schema of the repository (GC_Name = @@[GC_HeadDevice].@@[GC_HDGroup]).
        var example = ParameterSchemaJson.LoadFile(Path.Combine(AppContext.BaseDirectory, "examples", "schema.json"));
        Assert.False(example.HasErrors);
        var snapshot = new SchemaSnapshot(new SchemaNodReadResult(example.Schema, null, example.Issues));
        var values = XDataV2.Read(V2("B", Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "3")), snapshot);
        Assert.Equal("ЩУ1.3", new ObjectCalculation(values, Comma).Text("GC_Name"));
    }

    [Fact]
    public void CycleAndUnknownReferenceGiveDiagnostics()
    {
        var snapshot = Snapshot(Broken);
        var formulas = snapshot.Formulas;
        Assert.True(formulas.Find("A")!.InCycle);
        Assert.True(formulas.Find("B")!.InCycle);
        Assert.Contains("циклическая ссылка: A → B → A", formulas.Find("A")!.Errors);
        Assert.Contains("циклическая ссылка: B → A → B", formulas.Find("B")!.Errors);
        Assert.Contains("циклическая ссылка: Self → Self", formulas.Find("Self")!.Errors);
        Assert.False(formulas.Find("C")!.InCycle);
        Assert.Contains("зависит от ошибочного параметра A", formulas.Find("C")!.Errors);
        Assert.Equal(new[] { "неизвестная ссылка @@[Missing]" }, formulas.Find("U")!.Errors);
        Assert.Contains("незакрытая ссылка", formulas.Find("Bad")!.Error);
        Assert.Equal(new[] { "A", "B", "C", "Self", "U", "Bad" }.OrderBy(id => id), formulas.Diagnostics.Select(line => line.Split(':')[0]).OrderBy(id => id));
        // The schema validator, which refuses the import, finds the same cycles.
        var issues = new SchemaValidator().Validate(snapshot.Schema!);
        Assert.Contains(issues, issue => issue.Code == SchemaIssueCode.CyclicFormat && issue.Message.Contains("A → B → A"));

        // An unknown reference stays in the text; references inside a cycle have no value and stay as well.
        var calculation = Calculate(V2("X", Pair("S", "s")), snapshot);
        Assert.Equal("@@[Missing] и s", calculation.Text("U"));
        Assert.True(calculation.Find("U")!.IsError);
        Assert.Equal("@@[B]-a", calculation.Text("A"));
        Assert.Equal("@@[A]-b", calculation.Text("B"));
        Assert.Equal("@@[B]-a!", calculation.Text("C"));
        Assert.Equal("<@@[Self]>", calculation.Text("Self"));
        Assert.Equal("@@[S", calculation.Text("Bad"));

        // The palette diagnostics show the errors of the formulas the object uses (its template or stored values).
        var cache = new ObjectValuesCache(new SchemaCache(() => Load(Broken)));
        var selection = new PaletteViewModel().Selection;
        selection.Apply(new[] { V2("X", Pair(XDataV2.TemplateKey, "T"), Pair("S", "s")), V2("Y", Pair("S", "s")) }, null, cache);
        var warnings = selection.Entities[0].Warnings;
        Assert.Contains(warnings, warning => warning.StartsWith("A: циклическая ссылка: A → B → A", StringComparison.Ordinal));
        Assert.Contains("U: неизвестная ссылка @@[Missing].", warnings);
        Assert.DoesNotContain(warnings, warning => warning.StartsWith("Self:", StringComparison.Ordinal));
        Assert.DoesNotContain(selection.Entities[1].Warnings, warning => warning.Contains("ссылка"));
        Assert.Equal("@@[Missing] и s", selection.Calculations[0].Text("U"));
    }

    [Fact]
    public void ChangeOfASourceRecalculatesOnlyItsDependents()
    {
        var formulas = Snapshot().Formulas;
        Assert.Equal(new[] { "Name", "Full" }, formulas.Dependents("HD").Select(formula => formula.Id));
        Assert.Equal(new[] { "Name", "Full" }, formulas.Dependents("group").Select(formula => formula.Id));
        Assert.Equal(new[] { "Full" }, formulas.Dependents("Name").Select(formula => formula.Id));
        Assert.Equal(new[] { "Full", "Spec" }, formulas.Dependents("Voltage").Select(formula => formula.Id));
        Assert.Empty(formulas.Dependents("Other"));
        Assert.Same(formulas.Dependents("HD"), formulas.Dependents("HD"));

        var calculation = Calculate(V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3")));
        Assert.Equal(3, calculation.EvaluationCount);

        Assert.Equal(new[] { "Name", "Full" }, calculation.SetValue("HD", "ЩУ2").Select(result => result.Id));
        Assert.Equal(5, calculation.EvaluationCount);
        Assert.Equal("ЩУ2.3", calculation.Text("Name"));
        Assert.Equal("Щит ЩУ2.3 (220 В)", calculation.Text("Full"));

        var spec = calculation.Find("Spec");
        Assert.Equal(new[] { "Spec" }, calculation.SetValue("Power", 7.0).Select(result => result.Id));
        Assert.Equal(6, calculation.EvaluationCount);
        Assert.StartsWith("7,00 кВт", calculation.Text("Spec"));
        Assert.NotSame(spec, calculation.Find("Spec"));

        Assert.Empty(calculation.SetValue("Other", "x"));
        Assert.Equal(6, calculation.EvaluationCount);
        Assert.Throws<ArgumentException>(() => calculation.SetValue("Name", "вручную"));
    }

    [Fact]
    public void CalculationUsesTheValueCacheWithoutReadingXData()
    {
        var schema = new SchemaCache(() => Load(Schema));
        var cache = new ObjectValuesCache(schema);
        var snapshots = new[] { V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3")), V2("B", Pair("HD", "ЩУ1"), Pair("Group", "4")) };
        var values = cache.Get(snapshots);
        Assert.Equal(2, cache.ParseCount);

        var calculations = values.Select(item => new ObjectCalculation(item, Comma)).ToArray();
        // Formulas are parsed once per schema snapshot and shared by all objects.
        Assert.Same(schema.Get().Formulas, calculations[0].Formulas);
        Assert.Same(calculations[0].Formulas, calculations[1].Formulas);
        calculations[1].SetValue("Group", "3");
        Assert.Equal("ЩУ1.3", ObjectCalculation.Merge(calculations, "Name"));
        calculations[1].SetValue("Group", "5");
        Assert.Equal(1, schema.LoadCount);
        Assert.Equal(2, cache.ParseCount);

        // The palette builds its calculations from the same cache entries.
        var selection = new PaletteViewModel().Selection;
        selection.Apply(snapshots, null, cache);
        Assert.Equal(2, cache.ParseCount);
        Assert.Equal(new[] { "ЩУ1.3", "ЩУ1.4" }, selection.Calculations.Select(item => item.Text("Name")));
    }

    [Fact]
    public void TraceDescribesTheFormulasOfTheObject()
    {
        Assert.Equal("A: Name = «ЩУ1.3»; Full = «Щит ЩУ1.3 (220 В)»", Calculate(V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3"))).Describe());
        Assert.Equal("B: Name = «ЩУ1.3» *; Full = «Щит ЩУ1.3 (220 В)»",
            Calculate(V2("B", Pair("HD", "ЩУ1"), Pair("Group", "3"), Pair("Name", "старое"))).Describe());
        Assert.Equal("C: формул нет", Calculate(V2("C", Pair("Other", "x"))).Describe());
        Assert.Equal("D: U = «@@[Missing] и s» !", Calculate(V2("D", Pair("S", "s")), Snapshot(Broken)).Describe());
    }

    [Fact]
    public void MultipleSelectionShowsVariousWhenResultsDiffer()
    {
        var a = Calculate(V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3")));
        var b = Calculate(V2("B", Pair("HD", "ЩУ1"), Pair("Group", "4")));
        var c = Calculate(V2("C", Pair("HD", "ЩУ1"), Pair("Group", "3")));
        Assert.Equal("ЩУ1.3", ObjectCalculation.Merge(new[] { a, c }, "Name"));
        Assert.Null(ObjectCalculation.Common(new[] { a, b, c }, "Name"));
        Assert.Equal("Разное", ObjectCalculation.Merge(new[] { a, b, c }, "Name"));
        // Each object computes its own result: the common part is not merged into the formula text.
        Assert.Equal("Щит ЩУ1.3 (220 В)", ObjectCalculation.Merge(new[] { a, c }, "Full"));
        Assert.Equal(ObjectCalculation.Various, ObjectCalculation.Merge(new[] { a, b }, "Full"));
    }

    [Fact]
    public void ValuesAreSubstitutedBySection72()
    {
        var calculation = Calculate(V2("A", Pair("Power", 2.5), Pair("Ratio", 0.125), Pair("Count", 3), Pair("InSpec", true), Pair("Voltage", "_380"), Pair("Note", "п. 1")));
        Assert.Equal("2,50 кВт; 0,125; 3 шт; Да; _380; @@ п. 1", calculation.Text("Spec"));
        Assert.Equal("Щит . (380 В)", calculation.Text("Full"));
        var invariant = new ObjectCalculation(calculation.Source, CultureInfo.InvariantCulture);
        Assert.Equal("2.50 кВт; 0.125; 3 шт; Да; _380; @@ п. 1", invariant.Text("Spec"));

        // Missing values: the parameter default, otherwise empty; Boolean false is «Нет».
        var empty = Calculate(V2("B", Pair("InSpec", false)));
        Assert.Equal("0,00 кВт; ;  шт; Нет; _220; @@ нет", empty.Text("Spec"));
        // The template default overrides the parameter default.
        var device = Calculate(V2("C", Pair(XDataV2.TemplateKey, "DEVICE"), Pair("HD", "ЩУ1")));
        Assert.EndsWith("@@ шаблон", device.Text("Spec"));
        Assert.Equal("ЩУ1.1", device.Text("Name"));
        Assert.Equal(new[] { "Name", "Full", "Spec" }, device.SetTemplate(null).Select(result => result.Id));
        Assert.Equal("ЩУ1.", device.Text("Name"));
        // A removed value falls back to the default as well.
        device.SetValue("Note", "есть");
        Assert.EndsWith("@@ есть", device.Text("Spec"));
        device.SetValue("Note", null);
        Assert.EndsWith("@@ нет", device.Text("Spec"));

        // A key missing in the enum is substituted as is; a value of another type is shown as stored.
        var odd = Calculate(V2("D", Pair("Voltage", "_660"), new DataValue(1000, "Count"), new DataValue(1000, "три")));
        Assert.Equal("Щит . (_660)", odd.Text("Full"));
        Assert.Contains("; три шт;", odd.Text("Spec"));
    }

    [Fact]
    public void PersistIsWrittenWithTheChangeOfItsSource()
    {
        var patch = Patcher();
        var snapshot = patch.Apply(V2("A"), SelectionEdit.SetParameter("HD", "ЩУ1"));
        Assert.Equal(new[] { "HD", "ЩУ1", "Name", "ЩУ1." }, Payload(snapshot));
        snapshot = patch.Apply(snapshot, SelectionEdit.SetParameter("Group", "3"));
        Assert.Equal(new[] { "HD", "ЩУ1", "Name", "ЩУ1.3", "Group", "3" }, Payload(snapshot));
        // Full is not Persist and is never written; a change outside the formulas leaves Name as is.
        snapshot = patch.Apply(snapshot, SelectionEdit.SetParameter("Other", "x"));
        Assert.Equal(new[] { "HD", "ЩУ1", "Name", "ЩУ1.3", "Group", "3", "Other", "x" }, Payload(snapshot));
        snapshot = patch.Apply(snapshot, SelectionEdit.RemoveParameter("Group"));
        Assert.Equal(new[] { "HD", "ЩУ1", "Name", "ЩУ1.", "Other", "x" }, Payload(snapshot));

        // Removing a source of an object that does not store the result does not create it.
        var plain = patch.Apply(V2("B", Pair("HD", "ЩУ1")), SelectionEdit.RemoveParameter("HD"));
        Assert.Empty(Payload(plain));
        // The template brings its defaults and its Persist parameters.
        var device = patch.Apply(V2("C", Pair("HD", "ЩУ2")), SelectionEdit.SetTemplate("DEVICE"));
        Assert.Equal(new[] { "DEVICE_ID", "11111111-2222-3333-4444-555555555555", "TEMPLATE", "DEVICE", "HD", "ЩУ2", "Name", "ЩУ2.1" }, Payload(device));
        device = patch.Apply(device, SelectionEdit.SetTemplate("EMPTY"));
        Assert.Equal("ЩУ2.", Payload(device).Last());
    }

    [Fact]
    public void PersistIsWrittenInTheSameTransactionAndStoredValueIsDerived()
    {
        var database = new MemoryDatabase(V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3"), Pair("Name", "устарело")), V2("B", Pair("HD", "ЩУ2")));
        var cache = new ObjectValuesCache(new SchemaCache(() => Load(Schema)));
        var stored = cache.Get(database.Snapshots[0]);
        // On read the computed value is used; the stored one is only reported as stale.
        var calculation = new ObjectCalculation(stored, Comma);
        Assert.Equal("ЩУ1.3", calculation.Text("Name"));
        Assert.Equal("устарело", calculation.Find("Name")!.Stored);
        Assert.True(calculation.Find("Name")!.IsStale);

        new XDataWriter(database.Begin, cache).WriteSelection(new[] { "A", "B" }, SelectionEdit.SetParameter("Group", "7"));
        Assert.Equal(1, database.Commits);
        Assert.Equal(new[] { "HD", "ЩУ1", "Group", "7", "Name", "ЩУ1.7" }, Payload(database.Snapshots[0]));
        Assert.Equal(new[] { "HD", "ЩУ2", "Group", "7", "Name", "ЩУ2.7" }, Payload(database.Snapshots[1]));
        Assert.False(new ObjectCalculation(cache.Get(database.Snapshots[0])).Find("Name")!.IsStale);
        // Nothing is stale any more: the recalculation opens nothing for write.
        new XDataWriter(database.Begin, cache).WriteSelection(new[] { "A", "B" }, SelectionEdit.RecalculatePersisted());
        Assert.Equal(1, database.Commits);
    }

    [Fact]
    public void RecalculationRewritesStalePersistValues()
    {
        var patch = Patcher();
        var stale = V2("A", Pair("HD", "ЩУ1"), Pair("Group", "3"), Pair("Name", "устарело"), Pair("Other", "x"));
        Assert.Equal(new[] { "HD", "ЩУ1", "Group", "3", "Name", "ЩУ1.3", "Other", "x" }, Payload(patch.Apply(stale, SelectionEdit.RecalculatePersisted())));
        // The template lists Name: the missing value is created; an object without the template and the value is untouched.
        var device = V2("B", Pair(XDataV2.TemplateKey, "DEVICE"), Pair("HD", "ЩУ1"));
        Assert.Equal("ЩУ1.1", Payload(patch.Apply(device, SelectionEdit.RecalculatePersisted())).Last());
        var plain = V2("C", Pair("HD", "ЩУ1"));
        Assert.Equal(Payload(plain), Payload(patch.Apply(plain, SelectionEdit.RecalculatePersisted())));
        // Objects without XDATA v2 are skipped instead of refusing the whole recalculation.
        var v1 = Object("D", new DataValue(1001, "BobrovXDATA"), new DataValue(1000, "Name=x"));
        Assert.Same(v1, patch.Apply(v1, SelectionEdit.RecalculatePersisted()));
        var none = Object("E");
        Assert.Same(none, patch.Apply(none, SelectionEdit.RecalculatePersisted()));
    }

    [Fact]
    public void PersistLongerThan255BytesRefusesTheEdit()
    {
        var patch = Patcher();
        var snapshot = patch.Apply(V2("A"), SelectionEdit.SetParameter("HD", new string('Щ', 120)));
        var error = Assert.Throws<InvalidOperationException>(() => patch.Apply(snapshot, SelectionEdit.SetParameter("Group", new string('Г', 20))));
        Assert.Contains("Name (Persist)", error.Message);
        Assert.True(Encoding.UTF8.GetByteCount(new string('Щ', 120) + "." + new string('Г', 20)) > 255);
    }

    [Fact]
    public void ErroneousPersistFormulasAreNotWritten()
    {
        var patch = new XDataPatch(Snapshot(Broken), newGuid: () => Guid.Empty);
        var snapshot = patch.Apply(V2("A", Pair(XDataV2.TemplateKey, "T")), SelectionEdit.SetParameter("S", "s"));
        Assert.Equal(new[] { "TEMPLATE", "T", "S", "s" }, Payload(snapshot));
        Assert.Equal(Payload(snapshot), Payload(patch.Apply(snapshot, SelectionEdit.RecalculatePersisted())));
    }

    private static ObjectCalculation Calculate(EntityDataSnapshot snapshot, SchemaSnapshot? schema = null) =>
        new ObjectCalculation(XDataV2.Read(snapshot, schema ?? Snapshot()), Comma);

    private static CultureInfo DecimalComma()
    {
        // Built from the invariant culture so that the test does not depend on ICU data of the host.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    private static SchemaNodReadResult Load(string json)
    {
        var result = ParameterSchemaJson.Load(ParameterSchemaJsonTests.Json(json));
        Assert.NotNull(result.Schema);
        return new SchemaNodReadResult(result.Schema, null, result.Issues);
    }

    private static SchemaSnapshot Snapshot(string json = Schema) => new SchemaSnapshot(Load(json));
    private static XDataPatch Patcher() => new XDataPatch(Snapshot(), newGuid: () => new Guid("11111111-2222-3333-4444-555555555555"), culture: Comma);

    private static DataValue[] Pair(string id, object value) => new[]
    {
        new DataValue(1000, id),
        value switch
        {
            double number => new DataValue(1040, number),
            int number => new DataValue(1071, number),
            bool flag => new DataValue(1070, (short)(flag ? 1 : 0)),
            _ => new DataValue(1000, value)
        }
    };

    private static EntityDataSnapshot Object(string handle, params DataValue[] xdata) => new EntityDataSnapshot(handle, "Line", xdata, Array.Empty<DataRecord>());
    private static EntityDataSnapshot V2(string handle, params DataValue[][] pairs) =>
        Object(handle, new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1070, (short)2) }.Concat(pairs.SelectMany(pair => pair)).ToArray());
    private static EntityDataSnapshot V2(string handle, DataValue[] first, DataValue id, DataValue value) =>
        Object(handle, new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1070, (short)2) }.Concat(first).Concat(new[] { id, value }).ToArray());
    /// <summary>The v2 pairs after <c>(1070 . 2)</c> as text.</summary>
    private static string[] Payload(EntityDataSnapshot snapshot) => snapshot.XData.Skip(2).Select(value => XDataV2.FormatValue(value.Value)).ToArray();

    private sealed class MemoryDatabase
    {
        public MemoryDatabase(params EntityDataSnapshot[] snapshots) => Snapshots = snapshots;
        public EntityDataSnapshot[] Snapshots { get; private set; }
        public int Commits { get; private set; }
        public IXDataWriteTransaction Begin(IReadOnlyList<string> handles) => new Transaction(this, handles);

        private sealed class Transaction : IXDataWriteTransaction
        {
            private readonly MemoryDatabase database;
            private readonly List<EntityDataSnapshot> staged = new List<EntityDataSnapshot>();
            public Transaction(MemoryDatabase database, IReadOnlyList<string> handles)
            {
                this.database = database;
                Snapshots = database.Snapshots.Where(snapshot => handles.Contains(snapshot.Handle)).ToArray();
            }
            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }
            public void Write(EntityDataSnapshot snapshot) => staged.Add(snapshot);
            public void Commit()
            {
                database.Commits++;
                database.Snapshots = database.Snapshots.Select(snapshot => staged.FirstOrDefault(item => item.Handle == snapshot.Handle) ?? snapshot).ToArray();
            }
            public void Dispose() { }
        }
    }
}
