using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class SchemaValidatorTests
{
    private const string Root = "'format': 'BobrovXDATA.schema', 'version': 1";
    private const string Warning = "warning";

    /// <summary>Valid base: 7 parameters, enum L and template X; each case appends objects to one of the lists.</summary>
    private const string BaseParameters = @"
        { 'id': 'S', 'type': 'String', 'caption': 'Строка' },
        { 'id': 'D', 'type': 'Double', 'caption': 'Число', 'editor': { 'min': 0, 'max': 10 } },
        { 'id': 'I', 'type': 'Integer', 'caption': 'Целое' },
        { 'id': 'B', 'type': 'Boolean', 'caption': 'Флаг' },
        { 'id': 'E', 'type': 'Enum', 'caption': 'Список', 'enum': 'L' },
        { 'id': 'C', 'type': 'CalculatedString', 'caption': 'Формула', 'format': '@@[S]-@@[E:key] @@@@' },
        { 'id': 'T', 'type': 'Table', 'caption': 'Таблица', 'columns': ['E', 'S', 'D', 'C'], 'groupBy': 'E', 'key': 'S' }";
    private const string BaseEnums = "{ 'id': 'L', 'caption': 'Список', 'items': [ { 'key': 'a', 'caption': 'А' }, { 'key': 'b', 'caption': 'Б' } ] }";
    private const string BaseTemplates = "{ 'id': 'X', 'caption': 'Шаблон', 'parameters': [ 'S', { 'id': 'E', 'default': 'b' }, 'T' ] }";

    private static readonly string LongCyrillic = new string('Ж', 128);

    public static IEnumerable<object[]> Cases => new[]
    {
        // Ids (TZXDATA 4.5: unique within a section, allowed characters, reserved names).
        P("Required", "Параметр #8", "id", "{ 'type': 'String', 'caption': 'c' }"),
        P("Required", "Параметр #8", "id", "{ 'id': '', 'type': 'String', 'caption': 'c' }"),
        P("InvalidId", "Параметр 1abc", "id", "{ 'id': '1abc', 'type': 'String', 'caption': 'c' }"),
        P("InvalidId", "Параметр Мощность", "id", "{ 'id': 'Мощность', 'type': 'String', 'caption': 'c' }"),
        P("InvalidId", "Параметр Power kW", "id", "{ 'id': 'Power kW', 'type': 'String', 'caption': 'c' }"),
        P("InvalidId", "Параметр " + new string('A', 65), "id", "{ 'id': '" + new string('A', 65) + "', 'type': 'String', 'caption': 'c' }"),
        P("ReservedId", "Параметр DEVICE_ID", "id", "{ 'id': 'DEVICE_ID', 'type': 'String', 'caption': 'c' }"),
        P("ReservedId", "Параметр template", "id", "{ 'id': 'template', 'type': 'String', 'caption': 'c' }"),
        P("ReservedId", "Параметр Fmt", "id", "{ 'id': 'Fmt', 'type': 'String', 'caption': 'c' }"),
        P("DuplicateId", "Параметр s", "id", "{ 'id': 's', 'type': 'String', 'caption': 'c' }"),
        En("Required", "Справочник #2", "id", "{ 'caption': 'c', 'items': [] }"),
        En("InvalidId", "Справочник a-b", "id", "{ 'id': 'a-b', 'items': [] }"),
        En("DuplicateId", "Справочник l", "id", "{ 'id': 'l', 'items': [] }"),
        Tp("Required", "Шаблон #2", "id", "{ 'parameters': [] }"),
        Tp("InvalidId", "Шаблон X.1", "id", "{ 'id': 'X.1', 'parameters': [] }"),
        Tp("DuplicateId", "Шаблон x", "id", "{ 'id': 'x', 'parameters': [] }"),

        // Required parameter fields and the type registry.
        P("Required", "Параметр N", "caption", "{ 'id': 'N', 'type': 'String' }"),
        P("Required", "Параметр N", "type", "{ 'id': 'N', 'caption': 'c' }"),
        P("UnknownType", "Параметр N", "type", "{ 'id': 'N', 'type': 'Money', 'caption': 'c' }", Warning),

        // Enum references.
        P("Required", "Параметр N", "enum", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c' }"),
        P("UnknownReference", "Параметр N", "enum", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'Missing' }"),

        // Fields that do not apply to the type are kept but reported.
        P("NotApplicable", "Параметр N", "enum", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'enum': 'L' }", Warning),
        P("NotApplicable", "Параметр N", "format", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'format': '@@[S]' }", Warning),
        P("NotApplicable", "Параметр N", "persist", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'persist': true }", Warning),
        P("NotApplicable", "Параметр N", "columns", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'columns': ['S'] }", Warning),
        P("NotApplicable", "Параметр N", "groupBy", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'L', 'groupBy': 'E' }", Warning),
        P("NotApplicable", "Параметр N", "key", "{ 'id': 'N', 'type': 'Boolean', 'caption': 'c', 'key': 'S' }", Warning),
        P("NotApplicable", "Параметр N", "editor.precision", "{ 'id': 'N', 'type': 'Integer', 'caption': 'c', 'editor': { 'precision': 1 } }", Warning),
        P("NotApplicable", "Параметр N", "editor.min", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'editor': { 'min': 1 } }", Warning),
        P("NotApplicable", "Параметр N", "editor.max", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'L', 'editor': { 'max': 1 } }", Warning),
        P("NotApplicable", "Параметр N", "editor.maxLength", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'editor': { 'maxLength': 10 } }", Warning),
        P("NotApplicable", "Параметр N", "editor.multiline", "{ 'id': 'N', 'type': 'Boolean', 'caption': 'c', 'editor': { 'multiline': true } }", Warning),
        P("NotApplicable", "Параметр N", "editor.allowCustom", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'editor': { 'allowCustom': true } }", Warning),

        // Editor settings.
        P("InvalidEditor", "Параметр N", "editor.precision", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'editor': { 'precision': 16 } }"),
        P("InvalidEditor", "Параметр N", "editor.precision", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'editor': { 'precision': -1 } }"),
        P("InvalidEditor", "Параметр N", "editor.maxLength", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'editor': { 'maxLength': 0 } }"),
        P("InvalidEditor", "Параметр N", "editor.maxLength", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'editor': { 'maxLength': 256 } }"),
        P("InvalidEditor", "Параметр N", "editor.min", "{ 'id': 'N', 'type': 'Integer', 'caption': 'c', 'editor': { 'min': 5, 'max': 1 } }"),
        P("InvalidEditor", "Параметр N", "editor.vstColumn", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'editor': { 'vstColumn': 5 } }"),
        P("InvalidEditor", "Параметр N", "editor.vstColumn", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'editor': { 'vstColumn': 0 } }"),

        // Default must match the type (TZXDATA 4.5).
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'default': 5 }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'default': null }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'default': '" + LongCyrillic + "' }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'String', 'caption': 'c', 'default': 'abcd', 'editor': { 'maxLength': 3 } }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'default': '1.5' }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'default': -1, 'editor': { 'min': 0 } }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'default': 11, 'editor': { 'max': 10 } }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'default': 1e400 }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Integer', 'caption': 'c', 'default': 1.5 }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Integer', 'caption': 'c', 'default': 3000000000 }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Boolean', 'caption': 'c', 'default': 'true' }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Boolean', 'caption': 'c', 'default': 1 }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'L', 'default': 'z' }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'L', 'default': 'A' }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'L', 'default': 1 }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': 'x', 'default': false }"),
        P("InvalidDefault", "Параметр N", "default", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S'], 'default': [] }"),

        // CalculatedString format (TZXDATA 7).
        P("Required", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c' }"),
        P("InvalidFormat", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': 'x @@[S' }"),
        P("InvalidFormat", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[E:caption]' }"),
        P("InvalidFormat", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[1S]' }"),
        P("InvalidFormat", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[S:key]' }"),
        P("InvalidFormat", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[T]' }"),
        P("UnknownReference", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[S] @@[Missing]' }"),
        P("CyclicFormat", "Параметр N", "format", "{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '<@@[n]>' }"),
        P("CyclicFormat", "Параметр N1", "format",
            "{ 'id': 'N1', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[N2]' }, " +
            "{ 'id': 'N2', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[N3]-@@[S]' }, " +
            "{ 'id': 'N3', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[C]@@[N1]' }"),

        // Table (TZXDATA 4.5: no nested Table, no persisted CalculatedString).
        P("Required", "Параметр N", "columns", "{ 'id': 'N', 'type': 'Table', 'caption': 'c' }"),
        P("Required", "Параметр N", "columns", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': [] }"),
        P("UnknownReference", "Параметр N", "columns", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S', 'Missing'] }"),
        P("DuplicateReference", "Параметр N", "columns", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S', 's'] }"),
        P("InvalidColumn", "Параметр N", "columns", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S', 'T'] }"),
        P("InvalidColumn", "Параметр N", "columns",
            "{ 'id': 'CP', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[S]', 'persist': true }, " +
            "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S', 'CP'] }"),
        P("UnknownReference", "Параметр N", "groupBy", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S'], 'groupBy': 'E' }"),
        P("InvalidColumn", "Параметр N", "groupBy", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S', 'E'], 'groupBy': 'S' }"),
        P("UnknownReference", "Параметр N", "key", "{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['S'], 'key': 'D' }"),

        // Enum items.
        En("Required", "Справочник M / элемент #2", "key", "{ 'id': 'M', 'items': [ { 'key': 'a' }, { 'caption': 'Без ключа' } ] }"),
        En("InvalidValue", "Справочник M / элемент  a", "key", "{ 'id': 'M', 'items': [ { 'key': ' a' } ] }"),
        En("InvalidValue", "Справочник M / элемент " + LongCyrillic, "key", "{ 'id': 'M', 'items': [ { 'key': '" + LongCyrillic + "' } ] }"),
        En("DuplicateId", "Справочник M / элемент a", "key", "{ 'id': 'M', 'items': [ { 'key': 'a' }, { 'key': 'b' }, { 'key': 'a' } ] }"),

        // Templates.
        Tp("InvalidValue", "Шаблон Y", "entityTypes", "{ 'id': 'Y', 'entityTypes': ['Line', ' '], 'parameters': [] }"),
        Tp("Required", "Шаблон Y", "parameters", "{ 'id': 'Y', 'parameters': [ { 'default': 1 } ] }"),
        Tp("UnknownReference", "Шаблон Y", "parameters", "{ 'id': 'Y', 'parameters': [ 'S', 'Missing' ] }"),
        Tp("DuplicateReference", "Шаблон Y", "parameters", "{ 'id': 'Y', 'parameters': [ 'S', { 'id': 's', 'default': 'x' } ] }"),
        Tp("InvalidDefault", "Шаблон Y", "parameters", "{ 'id': 'Y', 'parameters': [ { 'id': 'E', 'default': 'zz' } ] }"),
        Tp("InvalidDefault", "Шаблон Y", "parameters", "{ 'id': 'Y', 'parameters': [ { 'id': 'D', 'default': 100 } ] }"),
        Tp("InvalidDefault", "Шаблон Y", "parameters", "{ 'id': 'Y', 'parameters': [ { 'id': 'T', 'default': [] } ] }"),
    };

    [Fact]
    public void BaseSchemaIsValid()
    {
        Assert.Empty(Load());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReportsEachProblemAsObjectFieldAndReason(string code, string objectPath, string field, string parameters, string enums, string templates, string severity)
    {
        var issues = Load(parameters, enums, templates);
        var issue = Assert.Single(issues);
        Assert.Equal(code, issue.Code.ToString());
        Assert.Equal(severity == Warning ? SchemaIssueSeverity.Warning : SchemaIssueSeverity.Error, issue.Severity);
        Assert.Equal(objectPath, issue.Object);
        Assert.Equal(field, issue.Field);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        Assert.Equal((severity == Warning ? "Предупреждение: " : "") + objectPath + " — " + field + " — " + issue.Message, issue.ToString());
    }

    [Fact]
    public void EveryValidationCodeIsCovered()
    {
        var covered = Cases.Select(item => (string)item[0]).Concat(new[] { "UnsupportedFormat", "UnsupportedVersion" });
        var readerCodes = new[] { SchemaIssueCode.InvalidJson, SchemaIssueCode.InvalidFieldType, SchemaIssueCode.FieldNameCase };
        var validatorCodes = System.Enum.GetValues(typeof(SchemaIssueCode)).Cast<SchemaIssueCode>().Except(readerCodes).Select(code => code.ToString());
        Assert.Empty(validatorCodes.Except(covered));
    }

    [Theory]
    [InlineData("'version': 1", "Required", "format", "error")]
    [InlineData("'format': 'SMARTLINE.schema', 'version': 1", "UnsupportedFormat", "format", "error")]
    [InlineData("'format': 'BobrovXDATA.schema'", "Required", "version", "error")]
    [InlineData("'format': 'BobrovXDATA.schema', 'version': 0", "UnsupportedVersion", "version", "error")]
    [InlineData("'format': 'BobrovXDATA.schema', 'version': 2", "UnsupportedVersion", "version", Warning)]
    public void ChecksFormatAndVersion(string root, string code, string field, string severity)
    {
        var issue = Assert.Single(Load(root: root));
        Assert.Equal((code, SchemaIssue.SchemaObject, field), (issue.Code.ToString(), issue.Object, issue.Field));
        Assert.Equal(severity == Warning ? SchemaIssueSeverity.Warning : SchemaIssueSeverity.Error, issue.Severity);
    }

    [Theory]
    [InlineData("{ 'id': 'N', 'type': 'Enum', 'caption': 'c', 'enum': 'l', 'default': 'custom', 'editor': { 'allowCustom': true } }")]
    [InlineData("{ 'id': 'N', 'type': 'string', 'caption': 'c', 'default': 'текст' }")]
    [InlineData("{ 'id': 'N', 'type': 'Integer', 'caption': 'c', 'default': 2.0, 'editor': { 'min': 2, 'max': 2 } }")]
    [InlineData("{ 'id': 'N', 'type': 'Double', 'caption': 'c', 'default': -1e-3, 'editor': { 'precision': 15, 'unit': 'м', 'readOnly': true } }")]
    [InlineData("{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': 'без ссылок @@ и @ [x]', 'default': '' }")]
    [InlineData("{ 'id': 'N', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[c]+@@[c]', 'persist': true }")]
    [InlineData("{ 'id': 'N', 'type': 'Table', 'caption': 'c', 'columns': ['s', 'B', 'I', 'E'], 'groupBy': 'e', 'key': 'S' }")]
    [InlineData("{ 'id': 'DEVICE', 'type': 'String', 'caption': 'c', 'group': '' }")]
    [InlineData("{ 'id': '_', 'type': 'String', 'caption': 'c', 'editor': { 'maxLength': 255, 'vstColumn': 4, 'multiline': true } }")]
    public void AcceptsValidParameters(string parameter)
    {
        Assert.Empty(Load(parameter));
    }

    [Fact]
    public void StringLimitIsCountedInUtf8Bytes()
    {
        var limit = new string('Ж', 127) + "z";
        Assert.Empty(Load("{ 'id': 'N', 'type': 'String', 'caption': 'c', 'default': '" + limit + "' }"));
        var issue = Assert.Single(Load("{ 'id': 'N', 'type': 'String', 'caption': 'c', 'default': '" + limit + "z' }"));
        Assert.Equal("строка длиннее 255 байт UTF-8", issue.Message);
    }

    [Fact]
    public void AcceptsEnumKeysThatDifferOnlyByCaseAndTemplatesWithEntityTypes()
    {
        Assert.Empty(Load(enums: "{ 'id': 'M', 'items': [ { 'key': 'a' }, { 'key': 'A' } ] }",
            templates: "{ 'id': 'Y', 'entityTypes': ['Polyline', 'Line'], 'parameters': [ { 'id': 'i', 'default': 7 }, 'B' ] }"));
    }

    [Fact]
    public void ReportsSeveralProblemsOfOneObject()
    {
        var issues = Load("{ 'id': 'FMT', 'type': 'Double', 'default': 'x', 'editor': { 'precision': 99 } }");
        Assert.Equal(new[] { "ReservedId", "Required", "InvalidEditor", "InvalidDefault" }, issues.Select(issue => issue.Code.ToString()));
        Assert.All(issues, issue => Assert.Equal("Параметр FMT", issue.Object));
    }

    [Fact]
    public void CycleMessageShowsThePath()
    {
        var issue = Assert.Single(Load("{ 'id': 'A1', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[B1]' }, " +
            "{ 'id': 'B1', 'type': 'CalculatedString', 'caption': 'c', 'format': '@@[a1]' }"));
        Assert.Equal("циклическая ссылка: A1 → B1 → A1", issue.Message);
    }

    [Fact]
    public void UnknownTypeIsAWarningAndCanBeRegistered()
    {
        const string money = "{ 'id': 'N', 'type': 'Money', 'caption': 'c', 'default': 'x' }";
        Assert.Equal(SchemaIssueSeverity.Warning, Assert.Single(Load(money)).Severity);
        var registry = ParameterTypeRegistry.CreateBuiltIn();
        registry.Register(new MoneyType());
        var issue = Assert.Single(Load(money, types: registry));
        Assert.Equal((SchemaIssueCode.InvalidDefault, "ожидается сумма"), (issue.Code, issue.Message));
        Assert.Null(ParameterTypeRegistry.Default.Find("Money"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Текст без ссылок", "T:Текст без ссылок")]
    [InlineData("@@[A]", "R:A")]
    [InlineData("x@@[A].@@[B:key]y", "T:x|R:A|T:.|R:B:key|T:y")]
    [InlineData("@@@@[A]", "T:@@[A]")]
    [InlineData("@@@@@@[A]", "T:@@|R:A")]
    [InlineData("@ @@ @@[", "T:@ @@ @@[")]
    public void ParsesCalculatedFormat(string format, string expected)
    {
        var fragments = CalculatedFormat.Parse(format, out var error);
        Assert.Equal(format.EndsWith("@@[") ? "незакрытая ссылка @@[ в позиции 5" : null, error);
        Assert.Equal(expected, string.Join("|", fragments.Select(fragment =>
            fragment.Reference is null ? "T:" + fragment.Text : "R:" + fragment.Reference + (fragment.UseKey ? ":key" : ""))));
    }

    private static object[] P(string code, string objectPath, string field, string parameters, string severity = "error") =>
        new object[] { code, objectPath, field, parameters, "", "", severity };

    private static object[] En(string code, string objectPath, string field, string enums, string severity = "error") =>
        new object[] { code, objectPath, field, "", enums, "", severity };

    private static object[] Tp(string code, string objectPath, string field, string templates, string severity = "error") =>
        new object[] { code, objectPath, field, "", "", templates, severity };

    private static IReadOnlyList<SchemaIssue> Load(string parameters = "", string enums = "", string templates = "", string root = Root, ParameterTypeRegistry? types = null)
    {
        static string Append(string items, string extra) => extra.Length == 0 ? items : items + ", " + extra;
        var json = "{ " + root +
            ", 'parameters': [" + Append(BaseParameters, parameters) + "]" +
            ", 'enums': [" + Append(BaseEnums, enums) + "]" +
            ", 'templates': [" + Append(BaseTemplates, templates) + "] }";
        var result = ParameterSchemaJson.Load(ParameterSchemaJsonTests.Json(json), types);
        Assert.NotNull(result.Schema);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == SchemaIssueCode.InvalidFieldType || issue.Code == SchemaIssueCode.InvalidJson);
        return result.Issues;
    }

    private sealed class MoneyType : IParameterType
    {
        public string Name => "Money";
        public ParameterKind Kind => ParameterKind.Double;
        public bool IsScalar => true;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema) => value is JsonNumber ? null : "ожидается сумма";
    }
}
