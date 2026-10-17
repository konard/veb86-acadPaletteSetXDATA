using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;
using static AcadPaletteSetXData.Core.Tests.SchemaMigrationTests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// TZXDATA stage 9: <c>VELEC_CHECK</c> — duplicates of <c>DEVICE_ID</c>, data outside the schema, <c>Enum</c> keys,
/// the limits of 255 bytes per string and 16 KB per object, and the recalculation of <c>Persist</c> over the drawing.
/// </summary>
public sealed class DrawingCheckTests
{
    private const string Id = "{0B1C2D3E-0000-4000-8000-000000000001}";

    [Fact]
    public void ACleanDrawingGivesOneLine()
    {
        var report = new DrawingCheck(Schema()).Run(new[]
        {
            Device("1A", Pair(XDataV2.DeviceIdKey, Id), Pair("NMO_Name", "Щит")),
            new EntityDataSnapshot("1B", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>())
        });
        Assert.True(report.IsClean);
        Assert.Equal(new[] { "Проверено объектов: 2; замечаний нет." }, report.Lines());
        Assert.Empty(report.Targets(DrawingFix.All));
    }

    [Fact]
    public void TheSmallerHandleKeepsADuplicatedDeviceIdAndTheCopiesGetNewGuids()
    {
        // 9F < 1A0 as numbers, though not as text.
        var drawing = new Drawing(Device("1A0", Pair(XDataV2.DeviceIdKey, Id)), Device("9F", Pair(XDataV2.DeviceIdKey, Id.ToLowerInvariant())),
            Device("2B0", Pair(XDataV2.DeviceIdKey, Id)));
        var check = new DrawingCheck(drawing.Values.Schema, newGuid: Guids());
        var report = check.Run(drawing.Snapshots);
        var duplicate = Assert.Single(report.Duplicates);
        Assert.Equal("9F", duplicate.Original);
        Assert.Equal(new[] { "1A0", "2B0" }, duplicate.Copies);
        Assert.Equal(new[] { "1A0", "2B0" }, report.Issues.Where(issue => issue.Kind == DrawingIssueKind.DuplicateDeviceId).Select(issue => issue.Handle));
        Assert.Contains("  " + duplicate.DeviceId + ": сохраняет 9F, новый GUID — 1A0, 2B0", report.Lines());
        Assert.Equal("Исправимо: дубли DEVICE_ID — объектов 2, Persist — объектов 0.", report.Lines().Last());
        Assert.Same(drawing.Snapshots[0], check.Fix(drawing.Snapshots[0], report, DrawingFix.Persist));

        var targets = report.Targets(DrawingFix.All);
        var written = drawing.WriteEach(targets, snapshot => check.Fix(snapshot, report, DrawingFix.Duplicates));
        Assert.Equal(2, written.Count);
        Assert.Equal(1, drawing.Commits);
        var ids = drawing.Snapshots.Select(snapshot => drawing.Read(snapshot).DeviceId).ToArray();
        Assert.Equal(Id.ToLowerInvariant(), ids[1]);
        Assert.Equal(3, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(new DrawingCheck(drawing.Values.Schema).Run(drawing.Snapshots).IsClean);
    }

    [Fact]
    public void DataOutsideTheSchemaAndTheLimitsAreReported()
    {
        var strict = Schema(text => text.Replace("\"editor\": { \"allowCustom\": true }", "\"editor\": { }"));
        var report = new DrawingCheck(strict).Run(new[]
        {
            Device("1", Pair(XDataV2.DeviceIdKey, Id), Pair("Unknown_Id", "x"), Materials(("Steel", "M1", 1, true, ""))),
            new EntityDataSnapshot("2", "Line", new[] { V(1001, "FOREIGN"), V(1000, new string('Ж', 130)) }, Array.Empty<DataRecord>()),
            Line("3"),
            Device("4", Pair(XDataV2.DeviceIdKey, "not-a-guid"), Pair("Power", "много"))
        });
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.NotInSchema && issue.Handle == "1" && issue.Id == "Unknown_Id");
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.UnknownEnumKey && issue.Id == "Materials/Mat_Category");
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.StringTooLong && issue.Handle == "2" && issue.Message.StartsWith("строка XDATA 260 байт", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.LegacyData && issue.Handle == "3" && issue.Message.Contains("VELEC_MIGRATE"));
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.TypeMismatch && issue.Handle == "4" && issue.Id == "Power");
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.Format && issue.Handle == "4");
        var lines = report.Lines();
        Assert.Contains("Данные вне схемы: 1", lines);
        Assert.Contains("Ключи вне справочника: 1", lines);
        Assert.Contains("Строки длиннее 255 байт: 1", lines);
        Assert.Contains("Прежние форматы (VELEC_MIGRATE): 1", lines);
        Assert.StartsWith("Проверено объектов: 4; замечаний: ", lines.Last());
        Assert.EndsWith("объектов с замечаниями: 4.", lines.Last());
        Assert.Empty(report.Targets(DrawingFix.All));
    }

    [Fact]
    public void LargeXDataIsReported()
    {
        var xdata = Device("1", Pair(XDataV2.DeviceIdKey, Id)).XData.ToList();
        for (var i = 0; i < 70; i++) xdata.AddRange(Pair("Note_" + i, new string('x', 240)));
        var report = new DrawingCheck(Schema()).Run(new[] { new EntityDataSnapshot("1", "Line", xdata, Array.Empty<DataRecord>()) });
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.XDataTooLarge);
        Assert.DoesNotContain(report.Issues, issue => issue.Kind == DrawingIssueKind.Format && issue.Message.StartsWith("Размер XDATA", StringComparison.Ordinal));
    }

    [Fact]
    public void StalePersistResultsAreRecalculatedOverTheDrawing()
    {
        var read = Persisted();
        var schema = new SchemaSnapshot(read);
        var drawing = new Drawing(new ObjectValuesCache(new SchemaCache(() => read)),
            Object("1", "DEVICE", Pair("HD", "ЩУ1"), Pair("Group", "3"), Pair("Name", "ЩУ1.3")),
            Object("2", "DEVICE", Pair("HD", "ЩУ2"), Pair("Group", "1"), Pair("Name", "старое")),
            Object("3", "DEVICE", Pair("HD", "ЩУ3"), Pair("Group", "2")));
        var check = new DrawingCheck(schema);
        var report = check.Run(drawing.Snapshots);
        Assert.Equal(new[] { "2", "3" }, report.StalePersist);
        Assert.Equal(new[] { "Name", "Name" }, report.Issues.Where(issue => issue.Kind == DrawingIssueKind.StalePersist).Select(issue => issue.Id));
        Assert.Equal("Исправимо: дубли DEVICE_ID — объектов 0, Persist — объектов 2.", report.Lines().Last());
        Assert.Same(drawing.Snapshots[1], check.Fix(drawing.Snapshots[1], report, DrawingFix.Duplicates));

        drawing.WriteEach(report.Targets(DrawingFix.Persist), snapshot => check.Fix(snapshot, report, DrawingFix.All));
        Assert.Equal(1, drawing.Commits);
        Assert.Equal(new object?[] { "ЩУ1.3", "ЩУ2.1", "ЩУ3.2" }, drawing.Stored("Name"));
        Assert.Empty(check.FixErrors);
        Assert.True(new DrawingCheck(schema).Run(drawing.Snapshots).IsClean);
    }

    [Fact]
    public void ADrawingWithoutASchemaIsCheckedForTheLimitsAndLegacyData()
    {
        var none = new SchemaSnapshot(new SchemaNodReadResult(null, null, Array.Empty<SchemaIssue>()));
        var report = new DrawingCheck(none).Run(new[] { Line("1"), Device("2", Pair(XDataV2.DeviceIdKey, Id)), Device("3", Pair(XDataV2.DeviceIdKey, Id)) });
        Assert.Contains(report.Issues, issue => issue.Kind == DrawingIssueKind.LegacyData);
        Assert.Equal(new[] { "3" }, report.Duplicates.Single().Copies);
        Assert.Empty(report.StalePersist);
    }

    [Fact]
    public void WriteEachRemovesRecordsAndRejectsForeignHandles()
    {
        var drawing = new Drawing(Cable("1"), Device("2", Pair("NMO_Name", "x")));
        // Records of a different count are a change even when the XDATA is the same.
        var written = drawing.WriteEach(new[] { "1", "2" },
            snapshot => snapshot.Records.Count == 0 ? snapshot : new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, snapshot.XData, Array.Empty<DataRecord>()));
        Assert.Equal(new[] { "1" }, written.Select(snapshot => snapshot.Handle));
        Assert.Empty(drawing.Snapshots[0].Records);
        // An equal copy is not written.
        Assert.Empty(drawing.WriteEach(new[] { "2" }, snapshot => new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, snapshot.XData.ToArray(), snapshot.Records)));
        Assert.Equal(1, drawing.Commits);
        var error = Assert.Throws<InvalidOperationException>(() => drawing.WriteEach(new[] { "1", "2" }, snapshot => drawing.Snapshots[1]));
        Assert.Contains("другого объекта", error.Message);
        Assert.Throws<InvalidOperationException>(() => drawing.WriteEach(new[] { "2" },
            snapshot => new EntityDataSnapshot("2", "Line", new[] { V(1001, DrawingNames.RegApp), V(1000, new string('Ж', 130)) }, Array.Empty<DataRecord>())));
        Assert.Equal(1, drawing.Commits);
    }

    [Fact]
    public void TheLegacyViewReadsEsmtSmartlineAndTheBackupButNotTypedPairs()
    {
        var esmt = new XDataParser().Parse(Line());
        Assert.Equal("Superline", esmt.Properties["Type"]);
        Assert.Equal("CD35", esmt.Materials.Single().Name);
        var smartline = new XDataParser().Parse(new EntityDataSnapshot("1", "Line", new[] { V(1001, DrawingNames.LegacyRegApp), V(1000, "Name=Старая") }, Array.Empty<DataRecord>()));
        Assert.Equal("Старая", smartline.Properties["Name"]);
        var backup = new XDataParser().Parse(new EntityDataSnapshot("2", "Line", new[] { V(1001, DrawingNames.BackupRegApp), V(1000, "Number=7") }, Array.Empty<DataRecord>()));
        Assert.Equal("7", backup.Properties["Number"]);
        var typed = new XDataParser().Parse(new EntityDataSnapshot("3", "Line",
            new[] { V(1001, DrawingNames.LegacyRegApp), V(1070, (short)2), V(1000, "Name=X"), V(1000, "Y") }, Array.Empty<DataRecord>()));
        Assert.Empty(typed.Properties);
        Assert.Empty(new XDataParser().Parse(Device("4", Pair("NMO_Name", "Name=X"))).Properties);
    }

    private static SchemaNodReadResult Persisted()
    {
        var result = ParameterSchemaJson.Load(ParameterSchemaJsonTests.Json(@"{ 'format': 'BobrovXDATA.schema', 'version': 1,
            'parameters': [
                { 'id': 'HD', 'type': 'String', 'caption': 'Головное устройство' },
                { 'id': 'Group', 'type': 'String', 'caption': 'Группа' },
                { 'id': 'Name', 'type': 'CalculatedString', 'caption': 'Обозначение', 'format': '@@[HD].@@[Group]', 'persist': true } ],
            'templates': [ { 'id': 'DEVICE', 'caption': 'Устройство', 'parameters': [ 'HD', 'Group', 'Name' ] } ] }"));
        Assert.NotNull(result.Schema);
        return new SchemaNodReadResult(result.Schema, null, result.Issues);
    }
}
