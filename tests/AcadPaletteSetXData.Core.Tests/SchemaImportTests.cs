using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class SchemaImportTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");
    private const string Enum = "{ 'id': 'L', 'caption': 'Список', 'items': [ { 'key': 'a', 'caption': 'А' } ] }";

    private static ParameterSchema Drawing() => SchemaNodTests.Schema(
        "{ 'id': 'Power', 'type': 'String', 'caption': 'Мощность' }, { 'id': 'Old', 'type': 'String', 'caption': 'Старый' }, { 'id': 'Same', 'type': 'Integer', 'caption': 'c' }",
        Enum, "{ 'id': 'X', 'caption': 'Шаблон', 'parameters': ['Power'] }");

    private static ParameterSchema Json() => SchemaNodTests.Schema(
        "{ 'id': 'power', 'type': 'Double', 'caption': 'Мощность, кВт' }, { 'id': 'Same', 'type': 'Integer', 'caption': 'c' }, { 'id': 'New', 'type': 'String', 'caption': 'Новый' }",
        Enum, "{ 'id': 'X', 'caption': 'Шаблон 2', 'parameters': ['Power', 'New'] }");

    [Fact]
    public void MergeAddsAndUpdatesButNeverDeletesAndKeepsTypeConflictsByDefault()
    {
        var plan = SchemaImport.Plan(Drawing(), Json(), SchemaImportMode.Merge);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(("power", "String", "Double", TypeConflictResolution.Keep), (conflict.Id, conflict.OldType, conflict.NewType, conflict.Resolution));

        var result = plan.BuildResult();
        Assert.Equal(new[] { "Power", "Old", "Same", "New" }, result.Parameters.Select(p => p.Id));
        Assert.Equal("String", result.FindParameter("Power")!.Type);
        Assert.Equal("Шаблон 2", result.FindTemplate("X")!.Caption);
        Assert.Equal(new[] { "Parameter Added New", "Template Changed X" }, plan.Changes.Select(Text));
        Assert.DoesNotContain(plan.Validate(), issue => issue.Severity == SchemaIssueSeverity.Error);

        conflict.Resolution = TypeConflictResolution.Replace;
        Assert.Equal(new[] { "power", "Old", "Same", "New" }, plan.BuildResult().Parameters.Select(p => p.Id));
        var change = plan.Changes.First();
        Assert.Equal("Parameter Changed power", Text(change));
        Assert.True(change.TypeChanged);
        Assert.Equal(("String", "Double"), (change.OldType, change.NewType));
    }

    [Fact]
    public void MergeTakesRootFieldsFromTheFile()
    {
        var drawing = Drawing();
        drawing.Extra.Add("author", new JsonString("старый"));
        drawing.Extra.Add("keep", JsonBoolean.True);
        var json = Json();
        json.Extra.Add("author", new JsonString("новый"));
        json.Version = 2;
        var result = SchemaImport.Plan(drawing, json, SchemaImportMode.Merge).BuildResult();
        Assert.Equal(2, result.Version);
        Assert.Equal("{ \"author\": \"новый\", \"keep\": true }", result.Extra.ToJson());
    }

    [Fact]
    public void ReplaceMakesTheDrawingSchemaEqualToTheFile()
    {
        var json = Json();
        var plan = SchemaImport.Plan(Drawing(), json, SchemaImportMode.Replace);
        Assert.Single(plan.Conflicts);
        Assert.Equal(ParameterSchemaJson.Write(json), ParameterSchemaJson.Write(plan.BuildResult()));
        Assert.Equal(new[] { "Parameter Changed power", "Parameter Added New", "Parameter Removed Old", "Template Changed X" }, plan.Changes.Select(Text));
    }

    [Fact]
    public void ImportIntoAnEmptyDrawingAddsEverything()
    {
        var example = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        foreach (var mode in new[] { SchemaImportMode.Merge, SchemaImportMode.Replace })
        {
            var plan = SchemaImport.Plan(null, example, mode);
            Assert.Empty(plan.Conflicts);
            Assert.Equal(ParameterSchemaJson.Write(example), ParameterSchemaJson.Write(plan.BuildResult()));
            Assert.All(plan.Changes, change => Assert.Equal(SchemaChangeKind.Added, change.Kind));
            Assert.Equal(example.Parameters.Count + example.Enums.Count + example.Templates.Count, plan.Changes.Count);
        }
    }

    [Fact]
    public void ThePlanDoesNotChangeItsInputs()
    {
        var drawing = Drawing();
        var json = Json();
        var before = (ParameterSchemaJson.Write(drawing), ParameterSchemaJson.Write(json));
        var plan = SchemaImport.Plan(drawing, json, SchemaImportMode.Merge);
        plan.Conflicts[0].Resolution = TypeConflictResolution.Replace;
        plan.BuildResult().Parameters.Clear();
        Assert.Equal(before, (ParameterSchemaJson.Write(drawing), ParameterSchemaJson.Write(json)));
    }

    [Fact]
    public void ValidationOfTheResultFindsBrokenReferences()
    {
        // The drawing template X refers to Power; the file removes it in Replace mode.
        var json = SchemaNodTests.Schema("{ 'id': 'Other', 'type': 'String', 'caption': 'c' }", "", "{ 'id': 'X', 'caption': 'Шаблон', 'parameters': ['Power'] }");
        var issues = SchemaImport.Plan(Drawing(), json, SchemaImportMode.Replace).Validate();
        Assert.Contains(issues, issue => issue.Severity == SchemaIssueSeverity.Error && issue.Code == SchemaIssueCode.UnknownReference);
    }

    [Fact]
    public void ReportListsChangesConflictsAndRemovedParametersInUse()
    {
        var usage = SchemaUsage.Scan(new[]
        {
            V2(("Old", new DataValue(1000, "x")), ("Power", new DataValue(1040, 2.5))),
            V2(("TEMPLATE", new DataValue(1000, "X")), ("old", new DataValue(1000, "y"))),
        });
        var plan = SchemaImport.Plan(Drawing(), Json(), SchemaImportMode.Replace);
        var report = SchemaImportReport.Format(plan, plan.Validate(), usage);
        Assert.Equal(new[]
        {
            "Режим: Заменить",
            "Параметры: добавлено 1, изменено 1, удалено 1",
            "  ~ power (тип String → Double) [используется объектами: 1]",
            "  + New",
            "  - Old [используется объектами: 2]",
            "Справочники: добавлено 0, изменено 0, удалено 0",
            "Шаблоны: добавлено 0, изменено 1, удалено 0",
            "  ~ X [используется объектами: 1]",
            "Конфликты типов: 1",
            "  power: в чертеже String, в файле Double — заменить [используется объектами: 1]",
            "Удаляются из схемы, но используются объектами: 1",
            "  Параметр Old — объектов: 2",
            "  Значения в объектах сохраняются и показываются как «Не описаны в схеме».",
        }, report);

        var merge = SchemaImport.Plan(Drawing(), Drawing(), SchemaImportMode.Merge);
        Assert.Equal(new[]
        {
            "Режим: Объединить",
            "Параметры: добавлено 0, изменено 0, удалено 0",
            "Справочники: добавлено 0, изменено 0, удалено 0",
            "Шаблоны: добавлено 0, изменено 0, удалено 0",
            "Схема чертежа не изменится.",
        }, SchemaImportReport.Format(merge, Array.Empty<SchemaIssue>()));

        var empty = SchemaImportReport.Format(SchemaImport.Plan(null, Json(), SchemaImportMode.Merge), Array.Empty<SchemaIssue>());
        Assert.Equal("Режим: Объединить (в чертеже нет схемы)", empty[0]);
    }

    [Fact]
    public void UsageReadsV2PairsTablesAndV1Strings()
    {
        var table = new List<DataValue>
        {
            new DataValue(1001, "BobrovXDATA"), new DataValue(1070, (short)2),
            new DataValue(1000, "DEVICE_ID"), new DataValue(1000, "3F2504E0"),
            new DataValue(1000, "TEMPLATE"), new DataValue(1000, "DEVICE"),
            new DataValue(1000, "Materials"), new DataValue(1002, "{"),
            new DataValue(1002, "{"), new DataValue(1000, "Mat_Mark"), new DataValue(1000, "CD35"), new DataValue(1000, "Mat_Count"), new DataValue(1040, 2.0), new DataValue(1002, "}"),
            new DataValue(1002, "{"), new DataValue(1000, "Mat_Mark"), new DataValue(1000, "CD20"), new DataValue(1002, "}"),
            new DataValue(1002, "}"),
            new DataValue(1000, "Poles"), new DataValue(1071, 3),
            new DataValue(1001, "OTHER"), new DataValue(1000, "Foreign"), new DataValue(1000, "x"),
        };
        var v1 = new List<DataValue>
        {
            new DataValue(1001, "OTHER"), new DataValue(1000, "Skip=1"),
            new DataValue(1001, "bobrovxdata"), new DataValue(1000, "Poles=4"), new DataValue(1002, "{"), new DataValue(1000, "Mat_Mark=CD35"), new DataValue(1002, "}"), new DataValue(1000, "no key"),
        };
        var foreign = new List<DataValue> { new DataValue(1001, "OTHER"), new DataValue(1000, "Poles=1") };
        var usage = SchemaUsage.Scan(new[] { table, v1, foreign });

        Assert.Equal(2, usage.ObjectCount);
        Assert.Equal(2, usage.Count(SchemaSection.Parameter, "poles"));
        Assert.Equal(2, usage.Count(SchemaSection.Parameter, "Mat_Mark"));
        Assert.Equal(1, usage.Count(SchemaSection.Parameter, "Materials"));
        Assert.Equal(1, usage.Count(SchemaSection.Parameter, "Mat_Count"));
        Assert.Equal(1, usage.Count(SchemaSection.Template, "device"));
        Assert.Equal(0, usage.Count(SchemaSection.Parameter, "DEVICE_ID"));
        Assert.Equal(0, usage.Count(SchemaSection.Parameter, "TEMPLATE"));
        Assert.Equal(0, usage.Count(SchemaSection.Parameter, "Foreign"));
        Assert.Equal(0, usage.Count(SchemaSection.Parameter, "Skip"));
        Assert.Equal(0, usage.Count(SchemaSection.Parameter, "no key"));
        Assert.Equal(0, usage.Count(SchemaSection.Enum, "L"));
    }

    [Fact]
    public void UsageToleratesDamagedLists()
    {
        var broken = new List<DataValue>
        {
            new DataValue(1001, "BobrovXDATA"), new DataValue(1070, 2),
            new DataValue(1002, "}"), new DataValue(1000, "Lost"),
        };
        var unclosed = new List<DataValue>
        {
            new DataValue(1001, "BobrovXDATA"), new DataValue(1070, 2),
            new DataValue(1040, 1.0), new DataValue(1000, "A"), new DataValue(1002, "{"), new DataValue(1002, "{"), new DataValue(1000, "B"), new DataValue(1000, "b"),
            new DataValue(1000, "C"),
        };
        var usage = SchemaUsage.Scan(new[] { broken, unclosed });
        Assert.Equal(2, usage.ObjectCount);
        Assert.Equal(1, usage.Count(SchemaSection.Parameter, "A"));
        Assert.Equal(1, usage.Count(SchemaSection.Parameter, "B"));
    }

    private static string Text(SchemaChange change) => change.Section + " " + change.Kind + " " + change.Id;

    private static IReadOnlyList<DataValue> V2(params (string Id, DataValue Value)[] pairs) =>
        new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1070, (short)2) }
            .Concat(pairs.SelectMany(pair => new[] { new DataValue(1000, pair.Id), pair.Value })).ToList();
}
