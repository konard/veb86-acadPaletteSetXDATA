using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// TZXDATA stage 9: <c>VELEC_MIGRATE</c> converts <c>ESMT_LEP_v1.0</c>, v1 strings of BobrovXDATA, XML
/// <c>VisualTreeString</c> and the earlier RegApp <c>SMARTLINE</c> to XDATA v2 by the table of the schema.
/// </summary>
public sealed class SchemaMigrationTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");
    private const string Xml1 = "<VisualTreeString><Properties><Type>Cable</Type><Name>Cable B</Name></Properties>";
    private const string Xml2 = "<Materials><Material Category='Armature' Name='CD35' Count='2' IsInSpec='false'/></Materials></VisualTreeString>";

    /// <summary>The LINE of <c>examples/stage3-selection.lsp</c>.</summary>
    internal static EntityDataSnapshot Line(string handle = "1F0") => new EntityDataSnapshot(handle, "Line", new[]
    {
        V(1001, DrawingNames.EsmtRegApp),
        V(1000, "Type=Superline"), V(1000, "Number=021"), V(1000, "Name=Line A"), V(1000, "ProjectReference=Project 021"),
        V(1000, "Material"), V(1002, "{"), V(1000, "Category=Steel"), V(1000, "Name=CD35"), V(1000, "Count=1,2"),
        V(1000, "IsInSpec=1"), V(1000, "Comment=Example"), V(1002, "}")
    }, Array.Empty<DataRecord>());

    /// <summary>The LWPOLYLINE of <c>examples/stage3-selection.lsp</c>: v1 strings and an XML XRecord of the extension dictionary.</summary>
    internal static EntityDataSnapshot Cable(string handle = "1F1") => new EntityDataSnapshot(handle, "Polyline",
        new[] { V(1001, DrawingNames.RegApp), V(1000, "Number=022") },
        new[] { new DataRecord(DrawingNames.RegApp, new[] { V(1, Xml1), V(1, Xml2) }) });

    [Fact]
    public void Stage3SelectionShowsTheSameValuesAndMaterialsAfterMigration()
    {
        var schema = Schema();
        var migration = new SchemaMigration(schema, removeSources: false, newGuid: Guids());
        foreach (var before in new[] { Line(), Cable() })
        {
            var legacy = new XDataParser().Parse(before);
            var result = migration.Migrate(before);
            Assert.True(result.Changed, before.Handle);
            Assert.True(result.Complete, string.Join("; ", result.Warnings));
            Assert.Empty(result.Errors);
            Assert.True(result.NewDeviceId);
            var values = XDataV2.Read(result.After, schema);
            Assert.Equal(XDataFormat.V2, values.Format);
            Assert.Empty(values.Warnings);

            // Every field of the legacy view is a value of the v2 VST.
            Assert.NotEmpty(legacy.Properties);
            foreach (var property in legacy.Properties)
            {
                var id = migration.Map.Field(property.Key, schema);
                Assert.NotNull(id);
                Assert.Equal(property.Value, values.Find(id!)!.Text);
            }
            // Every material is a row of Materials with the same fields.
            var rows = values.Find("Materials")!.Rows!;
            Assert.Equal(legacy.Materials.Count, rows.Count);
            foreach (var material in legacy.Materials)
            {
                var row = rows.Single(item => item.Key == material.Name);
                Assert.Equal(material.Category, row.Find("Mat_Category")!.Value);
                Assert.Equal(double.Parse(material.Count.Replace(',', '.'), CultureInfo.InvariantCulture), row.Find("Mat_Count")!.Value);
                Assert.Equal(material.IsInSpec, row.Find("Mat_InSpec")!.Value);
                Assert.Equal(string.IsNullOrEmpty(material.Comment) ? null : material.Comment, row.Find("Mat_Comment")?.Value);
            }
            // The kept sources still give the same legacy view: the VST of stage 3 is unchanged.
            var again = new XDataParser().Parse(result.After);
            Assert.Equal(legacy.Properties.OrderBy(item => item.Key), again.Properties.OrderBy(item => item.Key));
            Assert.Equal(legacy.Materials.Select(Text), again.Materials.Select(Text));
        }
    }

    [Fact]
    public void Stage3ValuesAreShownInThePropertiesTab()
    {
        var migration = new SchemaMigration(Schema(), removeSources: true, newGuid: Guids());
        var line = migration.Migrate(Line()).After;
        var cable = migration.Migrate(Cable()).After;
        var (selection, grid) = Model();
        selection.Apply(new[] { line }, cache: Cache());
        Assert.Equal("Superline", Row(grid, "Device_Type").Value);
        Assert.Equal("021", Row(grid, "Device_Number").Value);
        Assert.Equal("Line A", Row(grid, "NMO_Name").Value);
        Assert.Equal("Project 021", Row(grid, "Project_Ref").Value);
        Assert.Equal("1.2", TableRow(grid, "CD35").Value);

        selection.Apply(new[] { cable }, cache: Cache());
        Assert.Equal("Cable", Row(grid, "Device_Type").Value);
        Assert.Equal("022", Row(grid, "Device_Number").Value);
        Assert.Equal("Cable B", Row(grid, "NMO_Name").Value);
        Assert.Equal("2", TableRow(grid, "CD35").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedMigrationChangesNothing(bool removeSources)
    {
        var migration = new SchemaMigration(Schema(), removeSources, newGuid: Guids());
        foreach (var before in new[] { Line(), Cable() })
        {
            var first = migration.Migrate(before);
            Assert.True(first.Changed);
            var second = migration.Migrate(first.After);
            Assert.False(second.Changed, string.Join("; ", second.Warnings));
            Assert.Same(second.Before, second.After);
            Assert.Empty(second.Migrated);
            Assert.Empty(second.Errors);
            Assert.Empty(second.Warnings);
            Assert.Equal(removeSources, !second.HasLegacy);
        }
    }

    [Fact]
    public void SourcesAreKeptByDefaultAndV1StringsAreBackedUp()
    {
        var migration = new SchemaMigration(Schema(), removeSources: false, newGuid: Guids());
        var line = migration.Migrate(Line());
        Assert.Equal(new[] { DrawingNames.EsmtRegApp }, line.Sources);
        Assert.False(line.SourcesRemoved);
        // ESMT stays first and in place, v2 follows.
        Assert.Equal(Line().XData.Select(Text), line.After.XData.Take(Line().XData.Count).Select(Text));
        Assert.Equal(new[] { DrawingNames.EsmtRegApp, DrawingNames.RegApp }, Apps(line.After));

        var cable = migration.Migrate(Cable());
        Assert.Equal(new[] { DrawingNames.RegApp + " v1", "XML VisualTreeString (XRecord BobrovXDATA)" }, cable.Sources);
        Assert.Equal(new[] { DrawingNames.RegApp, DrawingNames.BackupRegApp }, Apps(cable.After));
        Assert.Equal(new[] { V(1001, DrawingNames.BackupRegApp), V(1000, "Number=022") }.Select(Text), cable.After.XData.Skip(cable.After.XData.Count - 2).Select(Text));
        Assert.Same(Cable().Records.Single().Values[0].Value, cable.After.Records.Single().Values[0].Value);
        Assert.Equal(XDataFormat.V2, XDataV2.FormatOf(cable.After.XData));
        Assert.Contains("Исходные данные сохраняются", SchemaMigration.Report(new[] { line, cable }, false).Last());
    }

    [Fact]
    public void TheFlagRemovesTheSourcesOfCompleteObjects()
    {
        var migration = new SchemaMigration(Schema(), removeSources: true, newGuid: Guids());
        var line = migration.Migrate(Line());
        Assert.True(line.SourcesRemoved);
        Assert.Equal(new[] { DrawingNames.RegApp }, Apps(line.After));
        var cable = migration.Migrate(Cable());
        Assert.True(cable.SourcesRemoved);
        Assert.Equal(new[] { DrawingNames.RegApp }, Apps(cable.After));
        Assert.Empty(cable.After.Records);
        Assert.Empty(SchemaMigration.FindSources(cable.After));
        var report = SchemaMigration.Report(new[] { line, cable }, true);
        Assert.Contains("  исходные данные удаляются", report);
        Assert.Contains("Объектов: 2, с прежними данными: 2, изменяется: 2, без изменений: 0, с ошибками: 0, перенесено не полностью: 0.", report);
    }

    [Fact]
    public void KeptSourcesAreRemovedByALaterRunWithTheFlag()
    {
        var keep = new SchemaMigration(Schema(), removeSources: false, newGuid: Guids());
        var remove = new SchemaMigration(Schema(), removeSources: true, newGuid: Guids());
        foreach (var before in new[] { Line(), Cable() })
        {
            var kept = keep.Migrate(before).After;
            var result = remove.Migrate(kept);
            Assert.True(result.Changed);
            Assert.True(result.SourcesRemoved);
            Assert.Empty(result.Migrated);
            Assert.False(result.NewDeviceId);
            Assert.Equal(new[] { DrawingNames.RegApp }, Apps(result.After));
            Assert.Empty(result.After.Records);
            Assert.Equal(XDataV2.Read(kept, Schema()).Values.Select(value => value.Id + "=" + value.Text),
                XDataV2.Read(result.After, Schema()).Values.Select(value => value.Id + "=" + value.Text));
            Assert.False(remove.Migrate(result.After).Changed);
        }
    }

    [Fact]
    public void AnIncompleteObjectKeepsItsSourcesEvenWithTheFlag()
    {
        var snapshot = Esmt("A", "Name=Опора", "Unknown_Field=1", "Odd", "Material", "{", "Name=X", "Mystery=2", "}");
        var result = new SchemaMigration(Schema(), removeSources: true, newGuid: Guids()).Migrate(snapshot);
        Assert.True(result.Changed);
        Assert.False(result.Complete);
        Assert.False(result.SourcesRemoved);
        Assert.Contains(result.Warnings, warning => warning.Contains("«Unknown_Field» не сопоставлено"));
        Assert.Contains(result.Warnings, warning => warning.Contains("«Mystery» строки Materials не сопоставлено"));
        Assert.Contains(result.Warnings, warning => warning.Contains("исходные данные сохраняются"));
        Assert.Equal(new[] { DrawingNames.EsmtRegApp, DrawingNames.RegApp }, Apps(result.After));
        var values = XDataV2.Read(result.After, Schema());
        Assert.Equal("Опора", values.Find("NMO_Name")!.Value);
        Assert.Equal("X", values.Find("Materials")!.Rows!.Single().Key);
        Assert.Contains("перенесено не полностью: 1.", SchemaMigration.Report(new[] { result }, true).Last(line => line.StartsWith("Объектов", StringComparison.Ordinal)));
    }

    [Fact]
    public void StoredV2ValuesAreNeverOverwritten()
    {
        var xdata = Device("A", Pair("Device_Number", "999"), Pair("NMO_Name", "Line A")).XData.ToList();
        xdata.AddRange(Esmt("A", "Number=021", "Name=Line A", "Title=Новый").XData);
        var before = new EntityDataSnapshot("A", "Line", xdata, Array.Empty<DataRecord>());
        var result = new SchemaMigration(Schema(), removeSources: false, newGuid: Guids()).Migrate(before);
        var values = XDataV2.Read(result.After, Schema());
        Assert.Equal("999", values.Find("Device_Number")!.Value);
        Assert.Equal("Новый", values.Find("Device_Title")!.Value);
        Assert.Equal("DEVICE", values.Template);
        Assert.Equal(new[] { "Device_Title" }, result.Migrated);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("Device_Number: в v2 уже записано другое значение", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("NMO_Name", StringComparison.Ordinal));
        Assert.False(migrationOf(result.After).Changed);

        static ObjectMigration migrationOf(EntityDataSnapshot snapshot) => new SchemaMigration(Schema(), false).Migrate(snapshot);
    }

    [Fact]
    public void LaterSourcesWinAndConflictsAreReported()
    {
        var snapshot = new EntityDataSnapshot("A", "Line", Esmt("A", "Name=ESMT").XData,
            new[] { new DataRecord("BobrovXDATA", new[] { V(1, "<VisualTreeString><Properties Name='XML'/></VisualTreeString>") }) });
        var result = new SchemaMigration(Schema(), false, newGuid: Guids()).Migrate(snapshot);
        Assert.Equal("XML", XDataV2.Read(result.After, Schema()).Find("NMO_Name")!.Value);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("NMO_Name: «ESMT»", StringComparison.Ordinal));
        // The parser of the legacy VST also lets the XML win.
        Assert.Equal("XML", new XDataParser().Parse(snapshot).Properties["Name"]);
    }

    [Fact]
    public void EnumCaptionsBecomeKeysAndFreeTextNeedsAllowCustom()
    {
        var snapshot = Esmt("A", "Material", "{", "Category=Линейная арматура", "Name=A1", "}", "Материал", "{", "Category=Concrete", "Name=A2", "}",
            "Material", "{", "Category=Свои", "Name=A3", "Count=x", "}");
        var result = new SchemaMigration(Schema(), false, newGuid: Guids()).Migrate(snapshot);
        var rows = XDataV2.Read(result.After, Schema()).Find("Materials")!.Rows!;
        Assert.Equal(new object[] { "LineFittings", "Concrete", "Свои" }, rows.Select(row => row.Find("Mat_Category")!.Value));
        Assert.Null(rows[2].Find("Mat_Count"));
        Assert.False(result.Complete);
        Assert.Contains(result.Warnings, warning => warning.Contains("Materials/Count: «x» не перенесено"));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("отсутствует в справочнике"));

        var strict = Schema(text => text.Replace("\"editor\": { \"allowCustom\": true }", "\"editor\": { }"));
        var warned = new SchemaMigration(strict, false, newGuid: Guids()).Migrate(snapshot);
        Assert.Contains(warned.Warnings, warning => warning.Contains("ключ «Свои» отсутствует в справочнике MaterialCategory"));
        Assert.Contains(new DrawingCheck(strict).Run(new[] { warned.After }).Issues, issue => issue.Kind == DrawingIssueKind.UnknownEnumKey);
    }

    [Fact]
    public void SmartlineStringsAndTypedPairsAreMigrated()
    {
        var strings = new EntityDataSnapshot("A", "Line", new[] { V(1001, DrawingNames.LegacyRegApp), V(1000, "Name=Старая"), V(1000, "Тип=Щит") }, Array.Empty<DataRecord>());
        var result = new SchemaMigration(Schema(), true, newGuid: Guids()).Migrate(strings);
        var values = XDataV2.Read(result.After, Schema());
        Assert.Equal(("Старая", "Щит"), (values.Find("NMO_Name")!.Value, values.Find("Device_Type")!.Value));
        Assert.True(result.SourcesRemoved);
        Assert.Equal(new[] { DrawingNames.LegacyRegApp }, result.Sources);

        var typed = new EntityDataSnapshot("B", "Line", new[]
        {
            V(1001, DrawingNames.LegacyRegApp), V(1070, (short)2), V(1000, XDataV2.DeviceIdKey), V(1000, "{0B1C2D3E-0000-4000-8000-000000000001}"),
            V(1000, "Power"), V(1040, 2.5), V(1000, "Old_Name"), V(1000, "Щит 1"),
            V(1000, "Materials"), V(1002, "{"), V(1002, "{"), V(1000, "Mat_Mark"), V(1000, "M1"), V(1000, "Old_Count"), V(1040, 3.0), V(1002, "}"), V(1002, "}")
        }, Array.Empty<DataRecord>());
        var renamed = Schema(Rename("\"Old_Name\": \"NMO_Name\", \"Old_Count\": \"Mat_Count\""));
        var moved = new SchemaMigration(renamed, false, newGuid: Guids()).Migrate(typed);
        values = XDataV2.Read(moved.After, renamed);
        Assert.Equal("{0B1C2D3E-0000-4000-8000-000000000001}", values.DeviceId);
        Assert.Equal(2.5, values.Find("Power")!.Value);
        Assert.Equal("Щит 1", values.Find("NMO_Name")!.Value);
        Assert.Null(values.Find("Old_Name"));
        Assert.False(moved.NewDeviceId);
        // Typed SMARTLINE pairs are copied as they are: the old cell Id is renamed by the second run in BobrovXDATA.
        Assert.Equal(new[] { DrawingNames.LegacyRegApp, DrawingNames.RegApp }, Apps(moved.After));
        var second = new SchemaMigration(renamed, false, newGuid: Guids()).Migrate(moved.After);
        Assert.Equal(3.0, XDataV2.Read(second.After, renamed).Find("Materials")!.Rows!.Single().Find("Mat_Count")!.Value);
        Assert.False(new SchemaMigration(renamed, false, newGuid: Guids()).Migrate(second.After).Changed);
    }

    [Fact]
    public void RenameMovesStoredValuesAndTableCellsOnlyThroughTheMigration()
    {
        var schema = Schema(Rename("\"Old_Name\": \"NMO_Name\", \"Old_Mark\": \"Mat_Mark\""));
        var before = Device("A", Pair("Old_Name", "Щит"), new[]
        {
            V(1000, "Materials"), V(1002, "{"), V(1002, "{"), V(1000, "Old_Mark"), V(1000, "M1"), V(1000, "Mat_Count"), V(1040, 2.0), V(1002, "}"), V(1002, "}")
        });
        Assert.Equal(ObjectValueState.NotInSchema, XDataV2.Read(before, schema).Find("Old_Name")!.State);
        var result = new SchemaMigration(schema, false, newGuid: Guids()).Migrate(before);
        Assert.Equal(new[] { "Old_Name → NMO_Name", "Materials/Old_Mark → Mat_Mark" }, result.Renamed);
        Assert.Empty(result.Sources);
        Assert.False(result.NewDeviceId); // Objects without legacy data get no DEVICE_ID here; VELEC_CHECK reports it.
        var values = XDataV2.Read(result.After, schema);
        Assert.Equal("Щит", values.Find("NMO_Name")!.Value);
        Assert.Equal("M1", values.Find("Materials")!.Rows!.Single().Key);
        Assert.Empty(values.Warnings);
        Assert.False(new SchemaMigration(schema, false).Migrate(result.After).Changed);

        // A value already stored under the new Id is kept; the old one stays for the user to resolve.
        var both = Device("B", Pair("Old_Name", "Старое"), Pair("NMO_Name", "Новое"));
        var kept = new SchemaMigration(schema, false).Migrate(both);
        Assert.False(kept.Changed);
        Assert.Contains(kept.Warnings, warning => warning.Contains("значение NMO_Name уже есть"));
    }

    [Fact]
    public void ObjectsBreakingTheLimitsAreLeftAsTheyAre()
    {
        var foreign = new List<DataValue> { V(1001, "FOREIGN") };
        foreign.AddRange(Enumerable.Range(0, 66).Select(i => V(1000, new string('x', 240))));
        var big = new EntityDataSnapshot("A", "Line", foreign.Concat(Esmt("A", "Name=" + new string('y', 200), "Title=" + new string('z', 200)).XData).ToArray(), Array.Empty<DataRecord>());
        var result = new SchemaMigration(Schema(), false, newGuid: Guids()).Migrate(big);
        Assert.False(result.Changed);
        Assert.Contains(result.Errors, error => error.Contains("XDATA превысит " + XDataV2.MaxXDataBytes));
        Assert.Contains("— не изменяется", SchemaMigration.Report(new[] { result }, false)[0]);

        // A string longer than 255 bytes of UTF-8 is not migrated: the source keeps it.
        var longText = Esmt("B", "Name=" + new string('Ж', 130));
        var partial = new SchemaMigration(Schema(), true, newGuid: Guids()).Migrate(longText);
        Assert.False(partial.Complete);
        Assert.False(partial.SourcesRemoved);
        Assert.Null(XDataV2.Read(partial.After, Schema()).Find("NMO_Name"));
    }

    [Fact]
    public void DamagedOrAmbiguousDataIsNotMigrated()
    {
        var migration = new SchemaMigration(Schema(), true, newGuid: Guids());
        var newer = new EntityDataSnapshot("A", "Line", new[] { V(1001, DrawingNames.RegApp), V(1070, (short)3), V(1001, DrawingNames.EsmtRegApp), V(1000, "Name=X") }, Array.Empty<DataRecord>());
        Assert.Contains("более новой версией", Assert.Single(migration.Migrate(newer).Errors));
        var backup = new EntityDataSnapshot("B", "Line", new[] { V(1001, DrawingNames.RegApp), V(1000, "Name=X"), V(1001, DrawingNames.BackupRegApp), V(1000, "Name=Y") }, Array.Empty<DataRecord>());
        var both = migration.Migrate(backup);
        Assert.False(both.Changed);
        Assert.Contains("копия", Assert.Single(both.Errors));
        var repeated = new EntityDataSnapshot("C", "Line", new[] { V(1001, DrawingNames.RegApp), V(1070, (short)2), V(1001, DrawingNames.RegApp), V(1000, "Name=X") }, Array.Empty<DataRecord>());
        Assert.Contains("повторяется", Assert.Single(migration.Migrate(repeated).Errors));
        var unclosed = Esmt("D", "Name=X", "Material", "{", "Name=M");
        var damaged = migration.Migrate(unclosed);
        Assert.False(damaged.Complete);
        Assert.False(damaged.SourcesRemoved);
        Assert.Throws<InvalidOperationException>(() => new SchemaMigration(new SchemaSnapshot(new SchemaNodReadResult(null, null, Array.Empty<SchemaIssue>())), false));
    }

    [Fact]
    public void ObjectsWithoutLegacyDataAreNotTouched()
    {
        var migration = new SchemaMigration(Schema(), true, newGuid: Guids());
        foreach (var snapshot in new[]
        {
            new EntityDataSnapshot("A", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>()),
            new EntityDataSnapshot("B", "Line", new[] { V(1001, "FOREIGN"), V(1000, "Name=X") }, new[] { new DataRecord("Other", new[] { V(1, "<Other/>") }) }),
            Device("C", Pair("NMO_Name", "X"))
        })
        {
            var result = migration.Migrate(snapshot);
            Assert.False(result.Changed, snapshot.Handle);
            Assert.False(result.HasLegacy);
        }
    }

    [Fact]
    public void TheWholeDrawingIsWrittenInOneTransactionAndMigratedRecordsAreRemoved()
    {
        var drawing = new Drawing(Line("1"), Cable("2"), Device("3", Pair("NMO_Name", "v2")));
        var migration = new SchemaMigration(drawing.Values.Schema, removeSources: true, newGuid: Guids());
        var written = drawing.WriteEach(drawing.Snapshots.Select(snapshot => snapshot.Handle).ToArray(), snapshot => migration.Migrate(snapshot).After);
        Assert.Equal(new[] { "1", "2" }, written.Select(snapshot => snapshot.Handle));
        Assert.Equal(1, drawing.Commits);
        Assert.Equal(2, drawing.Writes);
        Assert.Empty(drawing.Snapshots[1].Records);
        Assert.Equal(new object?[] { "Line A", "Cable B", "v2" }, drawing.Stored("NMO_Name"));
        // The repeated command finds nothing to write.
        Assert.Empty(drawing.WriteEach(drawing.Snapshots.Select(snapshot => snapshot.Handle).ToArray(), snapshot => migration.Migrate(snapshot).After));
        Assert.Equal(1, drawing.Commits);
    }

    [Fact]
    public void ValidatorChecksTheMigrationSection()
    {
        Assert.DoesNotContain(ParameterSchemaJson.LoadFile(ExamplePath).Issues, issue => issue.Object == "Миграция");
        var issues = Issues(@"'migration': {
            'fields': { 'A': 'Missing', 'B': 'DEVICE_ID', 'C': 'Materials', 'D': 'GC_Name', 'E': 1 },
            'tables': { 'M': { 'table': 'NMO_Name' }, 'N': { 'table': 'Materials', 'columns': { 'X': 'Power' }, 'other': 1 }, 'O': 5 },
            'rename': { 'Old': 'Missing', 'NMO_Name': 'NMO_Name', 'P': 'Q_Old', 'Q_Old': 'Power', 'Device_Type': 'Power', 'template': 'Power', '1x': 'Power' },
            'extra': {} }");
        Assert.Contains("Миграция — migration.fields.A — параметр \"Missing\" не описан в схеме", issues);
        Assert.Contains("Миграция — migration.fields.B — параметр \"DEVICE_ID\" не описан в схеме", issues);
        Assert.Contains("Миграция — migration.fields.C — поле переносится только в параметр со значением, не в Table", issues);
        Assert.Contains("Миграция — migration.fields.D — поле переносится только в параметр со значением, не в CalculatedString", issues);
        Assert.Contains("Миграция — migration.fields.E — ожидается строка с Id", issues);
        Assert.Contains("Миграция — migration.tables.M.table — параметр Table \"NMO_Name\" не описан в схеме", issues);
        Assert.Contains("Миграция — migration.tables.N.columns.X — \"Power\" не является колонкой Materials", issues);
        Assert.Contains("Предупреждение: Миграция — migration.tables.N.other — неизвестное поле не используется", issues);
        Assert.Contains("Миграция — migration.tables.O — ожидается объект", issues);
        Assert.Contains("Миграция — migration.rename.Old — параметр \"Missing\" не описан в схеме", issues);
        Assert.Contains("Миграция — migration.rename.NMO_Name — Id переименовывается в себя", issues);
        Assert.Contains("Миграция — migration.rename.P — параметр \"Q_Old\" не описан в схеме", issues);
        Assert.Contains("Предупреждение: Миграция — migration.rename.Device_Type — параметр \"Device_Type\" ещё описан в схеме; его значения будут переименованы", issues);
        Assert.Contains("Миграция — migration.rename.template — системный Id не переименовывается", issues);
        Assert.Contains("Миграция — migration.rename.1x — недопустимый Id", issues);
        Assert.Contains("Предупреждение: Миграция — migration.extra — неизвестное поле не используется", issues);
        Assert.Contains("Миграция — migration — ожидается объект", Issues("'migration': []"));

        var chain = Issues("'migration': { 'rename': { 'A_Old': 'B_Old', 'B_Old': 'Power' } }");
        Assert.Contains("Миграция — migration.rename.A_Old — параметр \"B_Old\" не описан в схеме", chain);
        var chained = Issues("'migration': { 'rename': { 'A_Old': 'Power', 'Power': 'Current' } }");
        Assert.Contains("Миграция — migration.rename.A_Old — цепочка переименований: \"Power\" тоже переименовывается", chained);
    }

    [Fact]
    public void TheMigrationSectionIsStoredInTheNodWithTheSchema()
    {
        // VELEC_MIGRATE reads the schema of the drawing: the section imported from JSON is kept in NOD/VELEC/INFO.
        var schema = Schema(Rename("\"Old_Field\": \"Device_Class\"")).Schema!;
        var read = SchemaNod.FromRecords(SchemaNod.ToRecords(schema, SchemaNodInfo.Now("schema.json", SchemaImportMode.Replace)));
        Assert.DoesNotContain(read.Issues, issue => issue.Severity == SchemaIssueSeverity.Error);
        var map = MigrationMap.Read(read.Schema);
        Assert.Equal(MigrationMap.Read(schema).Fields, map.Fields);
        Assert.Equal("Device_Type", map.Fields["Type"]);
        Assert.Equal("Mat_Mark", map.Tables["Material"].Columns["Name"]);
        Assert.Equal("Device_Class", map.Renames["Old_Field"]);
        Assert.Equal(ParameterSchemaJson.Write(schema), ParameterSchemaJson.Write(read.Schema!));

        // The rename of examples/tzxdata-stage9-migrate.lsp moves Old_Field of D3 to Device_Class.
        var snapshot = new SchemaSnapshot(read);
        var d3 = Device("230", Pair(XDataV2.DeviceIdKey, "{0B1C2D3E-0000-4000-8000-000000000002}"), Pair("Old_Field", "x"), Pair("Device_Type", "Щит"));
        var result = new SchemaMigration(snapshot, false).Migrate(d3);
        Assert.Equal(new[] { "Old_Field → Device_Class" }, result.Renamed);
        Assert.Equal("x", XDataV2.Read(result.After, snapshot).Find("Device_Class")!.Value);
        Assert.True(new DrawingCheck(snapshot).Run(new[] { result.After }).IsClean);
    }

    [Fact]
    public void TheMapSkipsWrongEntriesAndFollowsRenamedParameters()
    {
        var schema = Schema(text => text.Replace("\"Type\": \"Device_Type\"", "\"Type\": \"Materials\", \"Тип\": \"GC_Name\", \"Voltage\": \"Missing\""));
        var map = MigrationMap.Read(schema.Schema);
        Assert.Null(map.Field("Type", schema));
        Assert.Null(map.Field("Тип", schema));
        Assert.Equal("Voltage", map.Field("Voltage", schema)); // A legacy key equal to an Id needs no entry.
        Assert.Equal("NMO_Name", map.Field("Название", schema)); // Russian keys of the VST are canonical.
        Assert.Equal("Materials", map.Table("Материал", schema)!.Table);
        Assert.Null(map.Table("Other", schema));
        Assert.Equal("Mat_Mark", MigrationMap.Column(map.Table("Material", schema)!, "Марка", schema));

        var editor = new SchemaEditorViewModel(ParameterSchemaJson.LoadFile(ExamplePath).Schema!, culture: CultureInfo.InvariantCulture);
        editor.FindParameter("Mat_Mark")!.Id = "Mat_Brand";
        var renamed = MigrationMap.Read(editor.Draft);
        Assert.Equal("Mat_Brand", renamed.Tables["Material"].Columns["Name"]);
        Assert.DoesNotContain(new SchemaValidator().Validate(editor.Draft), issue => issue.Object == "Миграция");
    }

    private static string Text(MaterialItemViewModel material) =>
        string.Join("|", material.Category, material.Name, material.Count, material.IsInSpec, material.Comment);

    private static string Text(DataValue value) => value.TypeCode + ":" + XDataV2.FormatValue(value.Value);

    internal static string[] Apps(EntityDataSnapshot snapshot) => snapshot.XData.Where(value => value.TypeCode == 1001).Select(value => (string)value.Value).ToArray();

    /// <summary>ESMT strings: <c>{</c> and <c>}</c> are list brackets.</summary>
    internal static EntityDataSnapshot Esmt(string handle, params string[] strings)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.EsmtRegApp) };
        xdata.AddRange(strings.Select(text => text == "{" || text == "}" ? V(1002, text) : V(1000, text)));
        return new EntityDataSnapshot(handle, "Line", xdata, Array.Empty<DataRecord>());
    }

    internal static Func<Guid> Guids()
    {
        var next = 0;
        return () => new Guid(++next, 0x1111, 0x4222, 0x83, 0x33, 0, 0, 0, 0, 0, 0);
    }

    internal static SchemaSnapshot Schema(Func<string, string>? edit = null)
    {
        var text = File.ReadAllText(ExamplePath);
        var result = ParameterSchemaJson.Load(edit is null ? text : edit(text));
        Assert.NotNull(result.Schema);
        return new SchemaSnapshot(new SchemaNodReadResult(result.Schema, null, result.Issues));
    }

    private static Func<string, string> Rename(string entries) => text => text.Replace("\"rename\": {}", "\"rename\": { " + entries + " }");

    private static string[] Issues(string migration)
    {
        var text = File.ReadAllText(ExamplePath);
        var at = text.IndexOf("\"migration\"", StringComparison.Ordinal);
        var json = text.Substring(0, at) + ParameterSchemaJsonTests.Json(migration) + "\n}\n";
        var result = ParameterSchemaJson.Load(json);
        Assert.NotNull(result.Schema);
        return result.Issues.Where(issue => issue.Object == "Миграция").Select(issue => issue.ToString()).ToArray();
    }
}
