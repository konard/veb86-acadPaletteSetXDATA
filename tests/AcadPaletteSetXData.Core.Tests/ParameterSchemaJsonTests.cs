using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class ParameterSchemaJsonTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [Fact]
    public void ExampleSchemaHasNoIssuesAndIsWrittenBackByteForByte()
    {
        var text = File.ReadAllText(ExamplePath);
        var result = ParameterSchemaJson.Load(text);
        Assert.Empty(result.Issues);
        Assert.Equal(text, ParameterSchemaJson.Write(result.Schema!));
    }

    [Fact]
    public void ExampleSchemaCoversTheStageOneTemplatesEnumsAndMaterials()
    {
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        Assert.Equal(new[] { "DEVICE", "CABLE", "TRAY", "MOTOR" }, schema.Templates.Select(template => template.Id));
        Assert.Equal(new[] { "_AC_220V_50Hz", "_AC_380V_50Hz", "_DC_24V" }, schema.FindEnum("voltage")!.Items.Select(item => item.Key));
        Assert.Equal(new[] { "A", "B", "C", "ABC" }, schema.FindEnum("Phase")!.Items.Select(item => item.Key));
        var materials = schema.FindParameter("Materials")!;
        Assert.Equal("Table", materials.Type);
        Assert.Equal("Mat_Category", materials.GroupBy);
        Assert.Equal(5, materials.Columns!.Count);
        var motor = schema.FindTemplate("MOTOR")!;
        Assert.Equal("_AC_380V_50Hz", ((JsonString)motor.Parameters.Single(parameter => parameter.Id == "Voltage").Default!).Value);
        Assert.Equal(new[] { "BlockReference" }, motor.EntityTypes);
        var all = Enum.GetValues(typeof(ParameterKind)).Cast<ParameterKind>().Select(kind => kind.ToString());
        Assert.Equal(all.OrderBy(name => name), schema.Parameters.Select(parameter => parameter.Type!).Distinct().OrderBy(name => name));
    }

    [Fact]
    public void ReadsEveryKnownField()
    {
        var schema = Read(@"{ 'format': 'BobrovXDATA.schema', 'version': 1,
          'parameters': [
            { 'id': 'P', 'type': 'Double', 'caption': 'Мощность', 'description': 'd', 'group': 'g', 'default': 1.5,
              'editor': { 'unit': 'кВт', 'precision': 2, 'min': -1.5, 'max': 10, 'maxLength': 5, 'readOnly': true,
                          'multiline': false, 'allowCustom': true, 'vstColumn': 2 } },
            { 'id': 'E', 'type': 'Enum', 'caption': 'e', 'enum': 'L' },
            { 'id': 'C', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[P]', 'persist': true },
            { 'id': 'T', 'type': 'Table', 'caption': 't', 'columns': ['E', 'P'], 'groupBy': 'E', 'key': 'P' } ],
          'enums': [ { 'id': 'L', 'caption': 'Список', 'items': [ { 'key': 'k', 'caption': 'К' } ] } ],
          'templates': [ { 'id': 'X', 'caption': 'Шаблон', 'entityTypes': ['Line'], 'parameters': [ 'P', { 'id': 'E', 'default': 'k' } ] } ] }");
        Assert.Equal(ParameterSchema.FormatName, schema.Format);
        Assert.Equal(1, schema.Version);
        var p = schema.Parameters[0];
        Assert.Equal(("P", "Double", "Мощность", "d", "g"), (p.Id, p.Type, p.Caption, p.Description, p.Group));
        Assert.Equal("1.5", ((JsonNumber)p.Default!).Text);
        var editor = p.Editor!;
        Assert.Equal(("кВт", 2, -1.5, 10.0, 5, 2), (editor.Unit, editor.Precision, editor.Min, editor.Max, editor.MaxLength, editor.VstColumn));
        Assert.Equal((true, false, true), (editor.ReadOnly, editor.Multiline, editor.AllowCustom));
        Assert.Equal("L", schema.Parameters[1].Enum);
        Assert.Equal(("@@[P]", true), (schema.Parameters[2].Format, schema.Parameters[2].Persist));
        Assert.Equal(new[] { "E", "P" }, schema.Parameters[3].Columns);
        Assert.Equal(("E", "P"), (schema.Parameters[3].GroupBy, schema.Parameters[3].Key));
        Assert.Equal(("L", "Список", "k", "К"), (schema.Enums[0].Id, schema.Enums[0].Caption, schema.Enums[0].Items[0].Key, schema.Enums[0].Items[0].Caption));
        var template = schema.Templates[0];
        Assert.Equal(("X", "Шаблон"), (template.Id, template.Caption));
        Assert.Equal(new[] { "Line" }, template.EntityTypes);
        Assert.Equal(new[] { "P", "E" }, template.Parameters.Select(parameter => parameter.Id));
        Assert.Null(template.Parameters[0].Default);
        Assert.Equal("k", ((JsonString)template.Parameters[1].Default!).Value);
        Assert.Equal(ParameterSchema.DefaultGroup, new SchemaParameter().EffectiveGroup);
    }

    [Fact]
    public void NonCanonicalInputSurvivesRoundTripWithoutLoss()
    {
        // Fields in unusual order, unknown fields on every level, numbers in different notations.
        var source = Json(@"{ 'templates': [ { 'parameters': [ { 'default': 2.50, 'id': 'P', 'note': [1, {'x': null}] }, 'E' ],
              'id': 'T', 'future': { 'a': 1 } } ],
          'x-root': 'корень', 'version': 1, 'format': 'BobrovXDATA.schema',
          'enums': [ { 'items': [ { 'caption': 'Один', 'key': 'one', 'color': '#ff0000' } ], 'id': 'L', 'caption': 'L', 'sort': 'key' } ],
          'parameters': [
            { 'caption': 'Число', 'type': 'Double', 'id': 'P', 'default': 1e2, 'unit2': 'м',
              'editor': { 'min': 0.50, 'precision': 2.0, 'widget': 'slider' } },
            { 'id': 'E', 'type': 'Enum', 'caption': 'Список', 'enum': 'L', 'visibleIf': { 'P': { 'gt': 0 } } },
            { 'id': 'N', 'type': 'Money', 'caption': 'Тип новой версии', 'currency': 'RUB' } ] }");
        var first = ParameterSchemaJson.Load(source);
        Assert.False(first.HasErrors, string.Join("\n", first.Issues));
        var written = ParameterSchemaJson.Write(first.Schema!);
        Assert.True(JsonValue.DeepEquals(JsonValue.Parse(source), JsonValue.Parse(written)), written);
        // The canonical form is stable: writing it again gives the same text.
        Assert.Equal(written, ParameterSchemaJson.Write(ParameterSchemaJson.Read(written).Schema!));
        Assert.Contains("\"default\": 2.50", written);
        Assert.Contains("\"default\": 1e2", written);
        Assert.Contains("\"x-root\": \"корень\"", written);
        Assert.EndsWith("}\n", written);
        Assert.DoesNotContain("\r", written);
    }

    [Fact]
    public void UnknownFieldsArePreservedInExtraOnEveryLevel()
    {
        var schema = Read(@"{ 'format': 'BobrovXDATA.schema', 'version': 1, 'r': 1,
          'parameters': [ { 'id': 'P', 'type': 'String', 'caption': 'c', 'p': 2, 'editor': { 'e': 3 } } ],
          'enums': [ { 'id': 'L', 'caption': 'c', 'n': 4, 'items': [ { 'key': 'k', 'caption': 'c', 'i': 5 } ] } ],
          'templates': [ { 'id': 'T', 't': 6, 'parameters': [ { 'id': 'P', 'tp': 7 } ] } ] }");
        Assert.Equal("1", Extra(schema, "r"));
        Assert.Equal("2", Extra(schema.Parameters[0], "p"));
        Assert.Equal("3", Extra(schema.Parameters[0].Editor!, "e"));
        Assert.Equal("4", Extra(schema.Enums[0], "n"));
        Assert.Equal("5", Extra(schema.Enums[0].Items[0], "i"));
        Assert.Equal("6", Extra(schema.Templates[0], "t"));
        Assert.Equal("7", Extra(schema.Templates[0].Parameters[0], "tp"));
        var written = ParameterSchemaJson.Write(schema);
        foreach (var name in new[] { "r", "p", "e", "n", "i", "t", "tp" }) Assert.Contains("\"" + name + "\":", written);
    }

    [Theory]
    [InlineData("{ 'format': 5, 'version': 1 }", "Схема", "format", "ожидается строка")]
    [InlineData("{ 'format': 'BobrovXDATA.schema', 'version': 1.5 }", "Схема", "version", "ожидается целое число")]
    [InlineData("{ 'version': 1, 'parameters': {} }", "Схема", "parameters", "ожидается массив")]
    [InlineData("{ 'version': 1, 'parameters': [ 'P' ] }", "Схема", "parameters", "ожидается массив объектов")]
    [InlineData("{ 'parameters': [ { 'id': 'P', 'caption': 7 } ] }", "Параметр P", "caption", "ожидается строка")]
    [InlineData("{ 'parameters': [ { 'id': 5 } ] }", "Параметр #1", "id", "ожидается строка")]
    [InlineData("{ 'parameters': [ { 'id': 'P', 'persist': 'yes' } ] }", "Параметр P", "persist", "ожидается true или false")]
    [InlineData("{ 'parameters': [ { 'id': 'P', 'columns': [ 1 ] } ] }", "Параметр P", "columns", "ожидается массив строк")]
    [InlineData("{ 'parameters': [ { 'id': 'P', 'editor': [] } ] }", "Параметр P", "editor", "ожидается объект")]
    [InlineData("{ 'parameters': [ { 'id': 'P', 'editor': { 'min': '0' } } ] }", "Параметр P", "editor.min", "ожидается число")]
    [InlineData("{ 'parameters': [ { 'id': 'P', 'editor': { 'precision': 1e10 } } ] }", "Параметр P", "editor.precision", "ожидается целое число")]
    [InlineData("{ 'enums': [ { 'id': 'L', 'items': [ { 'key': true } ] } ] }", "Справочник L / элемент #1", "key", "ожидается строка")]
    [InlineData("{ 'enums': [ { 'id': 'L', 'items': 'a,b' } ] }", "Справочник L", "items", "ожидается массив")]
    [InlineData("{ 'templates': [ { 'id': 'T', 'entityTypes': 'Line', 'parameters': [] } ] }", "Шаблон T", "entityTypes", "ожидается массив строк")]
    [InlineData("{ 'templates': [ { 'id': 'T', 'parameters': [ 'P', 1 ] } ] }", "Шаблон T", "parameters", "ожидается массив строк или объектов")]
    [InlineData("{ 'templates': [ { 'id': 'T', 'parameters': [ { 'id': [] } ] } ] }", "Шаблон T / параметр #1", "id", "ожидается строка")]
    public void FieldOfWrongTypeIsReportedAndPreserved(string fragment, string objectPath, string field, string reason)
    {
        var source = Json(fragment);
        var result = ParameterSchemaJson.Read(source);
        var issue = Assert.Single(result.Issues);
        Assert.Equal((SchemaIssueCode.InvalidFieldType, SchemaIssueSeverity.Error), (issue.Code, issue.Severity));
        Assert.Equal((objectPath, field), (issue.Object, issue.Field));
        Assert.StartsWith(reason + ";", issue.Message);
        var written = ParameterSchemaJson.Write(result.Schema!);
        var sourceWithDefaults = WithRootDefaults(JsonValue.Parse(source));
        Assert.True(JsonValue.DeepEquals(sourceWithDefaults, JsonValue.Parse(written)), written);
    }

    [Fact]
    public void FieldNameInWrongCaseIsWarnedAndKept()
    {
        var result = ParameterSchemaJson.Read(Json("{ 'format': 'BobrovXDATA.schema', 'version': 1, 'parameters': [ { 'Id': 'P', 'editor': { 'MaxLength': 3 } } ] }"));
        Assert.Equal(new[] { ("Параметр #1", "Id"), ("Параметр #1", "editor.MaxLength") },
            result.Issues.Select(issue => (issue.Object, issue.Field)));
        Assert.All(result.Issues, issue => Assert.Equal((SchemaIssueCode.FieldNameCase, SchemaIssueSeverity.Warning), (issue.Code, issue.Severity)));
        Assert.Null(result.Schema!.Parameters[0].Id);
        Assert.Equal("\"P\"", Extra(result.Schema.Parameters[0], "Id"));
    }

    [Theory]
    [InlineData("{ 'a': ", "InvalidJson")]
    [InlineData("[ 1, 2 ]", "корень JSON должен быть объектом")]
    [InlineData("{ 'a': 1, 'a': 2 }", "Повторяющееся поле")]
    public void InvalidJsonGivesNoSchema(string fragment, string reason)
    {
        var result = ParameterSchemaJson.Load(Json(fragment));
        Assert.Null(result.Schema);
        Assert.True(result.HasErrors);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SchemaIssueCode.InvalidJson, issue.Code);
        Assert.Contains(reason == "InvalidJson" ? "строка 1" : reason, issue.Message);
    }

    [Fact]
    public void MissingListsAreWrittenAsEmptyArrays()
    {
        var written = ParameterSchemaJson.Write(ParameterSchemaJson.Read("{\"format\":\"BobrovXDATA.schema\",\"version\":1}").Schema!);
        Assert.Equal("{\n  \"format\": \"BobrovXDATA.schema\",\n  \"version\": 1,\n  \"parameters\": [],\n  \"enums\": [],\n  \"templates\": []\n}\n", written);
    }

    [Fact]
    public void SchemaBuiltInCodeIsWrittenAndReadBack()
    {
        var schema = new ParameterSchema();
        schema.Parameters.Add(new SchemaParameter { Id = "Voltage", Type = "Enum", Caption = "Напряжение", Enum = "Voltage", Default = new JsonString("_AC_220V_50Hz") });
        schema.Parameters.Add(new SchemaParameter { Id = "Length", Type = "Double", Caption = "Длина", Editor = new ParameterEditor { Min = 0.25, Precision = 2 } });
        var voltage = new SchemaEnum { Id = "Voltage", Caption = "Напряжение" };
        voltage.Items.Add(new SchemaEnumItem("_AC_220V_50Hz", "220 В"));
        schema.Enums.Add(voltage);
        var template = new SchemaTemplate { Id = "CABLE", Caption = "Кабель" };
        template.Parameters.Add(new TemplateParameter("Voltage", new JsonString("_AC_220V_50Hz")));
        template.Parameters.Add(new TemplateParameter("Length"));
        schema.Templates.Add(template);
        Assert.Empty(new SchemaValidator().Validate(schema));

        var previous = CultureInfo.CurrentCulture;
        string written;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            written = ParameterSchemaJson.Write(schema);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Contains("\"editor\": { \"precision\": 2, \"min\": 0.25 }", written);
        Assert.Contains("{ \"id\": \"Voltage\", \"default\": \"_AC_220V_50Hz\" },\n", written);
        var again = ParameterSchemaJson.Load(written);
        Assert.Empty(again.Issues);
        Assert.Equal(written, ParameterSchemaJson.Write(again.Schema!));
    }

    [Fact]
    public void FileIsWrittenAsUtf8WithoutBomAndReadWithBom()
    {
        var path = Path.Combine(Path.GetTempPath(), "schema-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
            ParameterSchemaJson.WriteFile(path, schema);
            var bytes = File.ReadAllBytes(path);
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal(File.ReadAllBytes(ExamplePath), bytes);
            File.WriteAllText(path, File.ReadAllText(ExamplePath), new UTF8Encoding(true));
            var withBom = ParameterSchemaJson.LoadFile(path);
            Assert.Empty(withBom.Issues);
            Assert.Equal("Напряжение", withBom.Schema!.FindParameter("Voltage")!.Caption);
        }
        finally { File.Delete(path); }
    }

    private static ParameterSchema Read(string fragment)
    {
        var result = ParameterSchemaJson.Read(Json(fragment));
        Assert.Empty(result.Issues);
        return result.Schema!;
    }

    private static string Extra(SchemaElement element, string name) => element.Extra[name]!.ToJson();

    /// <summary>Writes compact test JSON with single quotes.</summary>
    internal static string Json(string fragment) => fragment.Replace('\'', '"');

    /// <summary>The writer always emits the three lists; add them to the expected JSON when absent.</summary>
    private static JsonObject WithRootDefaults(JsonValue source)
    {
        var result = new JsonObject();
        foreach (var property in (JsonObject)source) result.Add(property.Key, property.Value);
        foreach (var name in new[] { "parameters", "enums", "templates" })
            if (!result.ContainsKey(name)) result.Add(name, new JsonArray());
        return result;
    }
}
