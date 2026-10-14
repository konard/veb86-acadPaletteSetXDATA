using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class SchemaNodTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");
    private static readonly SchemaNodInfo Stamp = new SchemaNodInfo("2026-10-14T12:00:00Z", @"C:\Проект\schema.json", "Replace");
    private static readonly int[] AllowedCodes = { 1, 2, 40, 90, 290, 300, 302, 303 };

    [Fact]
    public void ExampleSchemaRoundTripsThroughRecordsByteForByte()
    {
        var text = File.ReadAllText(ExamplePath);
        var schema = ParameterSchemaJson.Load(text).Schema!;
        var records = SchemaNod.ToRecords(schema, Stamp);

        var read = SchemaNod.FromRecords(records);
        Assert.Empty(read.Issues);
        Assert.True(read.Exists);
        Assert.False(read.IsDamaged);
        Assert.Equal(text, ParameterSchemaJson.Write(read.Schema!));
        Assert.Equal((Stamp.ImportedAt, Stamp.Source, Stamp.Mode), (read.Info!.ImportedAt, read.Info.Source, read.Info.Mode));
        Assert.Equal(SchemaNod.StorageVersion, read.Info.StorageVersion);
        Assert.Equal(SchemaNod.ComputeHash(schema), read.Info.Hash);
        AssertDxfSafe(records);
    }

    [Fact]
    public void EveryObjectIsOneRecordUnderItsSection()
    {
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        var names = SchemaNod.ToRecords(schema, Stamp).Select(record => record.Name).ToList();
        var expected = new[] { "INFO" }
            .Concat(schema.Parameters.Select(p => "PARAMETERS/" + p.Id))
            .Concat(schema.Enums.Select(e => "ENUMS/" + e.Id))
            .Concat(schema.Templates.Select(t => "TEMPLATES/" + t.Id));
        Assert.Equal(expected, names);

        var power = SchemaNod.ToRecords(schema, Stamp).Single(record => record.Name == "PARAMETERS/Power").Values;
        Assert.Equal((2, "order"), (power[0].TypeCode, power[0].Value));
        Assert.Equal((1, "id"), (power[2].TypeCode, power[2].Value));
        Assert.Equal((300, "Power"), (power[3].TypeCode, power[3].Value));
        Assert.Contains(power, value => value.TypeCode == 1 && (string)value.Value == "type");
    }

    [Fact]
    public void TypedValuesUseTypedCodesOnlyWhenTheTextIsRestoredExactly()
    {
        var schema = Schema(@"{ 'id': 'P', 'type': 'Double', 'caption': 'c', 'default': 2.5, 'persist': true, 'editor': { 'precision': 2, 'min': -0.1, 'max': 10 } }");
        var values = Fields(SchemaNod.ToRecords(schema, Stamp).Single(record => record.Name == "PARAMETERS/P"));
        Assert.Equal((40, (object)2.5), values["default"]);
        Assert.Equal((290, (object)true), values["persist"]);
        Assert.Equal((302, (object)"{\"precision\":2,\"min\":-0.1,\"max\":10}"), values["editor"]);

        foreach (var (json, code) in new[] { ("1", 90), ("-7", 90), ("2.5", 40), ("0.1", 40), ("1e300", 302), ("2.50", 302), ("-0", 302), ("0", 90),
            ("0.0", 302), ("12345678901", 40), ("123456789012345678", 302), ("0.30000000000000004", 302), ("1e400", 302), ("true", 290), ("null", 302), ("\"\"", 300) })
        {
            var s = Schema("{ 'id': 'P', 'type': 'Double', 'caption': 'c', 'default': " + json.Replace("\"", "'") + " }");
            var records = SchemaNod.ToRecords(s, Stamp);
            Assert.Equal(code, Fields(records[1])["default"].Code);
            var back = SchemaNod.FromRecords(records).Schema!;
            Assert.Equal(json, back.Parameters[0].Default!.ToJson());
        }
    }

    [Theory]
    [InlineData("строка с переводом\nстроки")]
    [InlineData(@"C:\путь\к\файлу")]
    [InlineData(@"\U+0041 и \M+1A2B")]
    [InlineData("^J и ^M")]
    [InlineData(" пробел в начале")]
    [InlineData("пробел в конце ")]
    [InlineData("\t")]
    [InlineData("\ud83d")]
    [InlineData("кавычки \" и { скобки }")]
    public void TextThatDxfCouldAlterIsStoredAsEscapedJson(string text)
    {
        var schema = Schema("{ 'id': 'P', 'type': 'String', 'caption': 'c' }");
        schema.Parameters[0].Description = text;
        var records = SchemaNod.ToRecords(schema, Stamp);
        AssertDxfSafe(records);
        Assert.Equal(text, SchemaNod.FromRecords(records).Schema!.Parameters[0].Description);
    }

    [Fact]
    public void SchemaSurvivesADxfTextRoundTrip()
    {
        // TZXDATA stage 2 acceptance without AutoCAD: records → DXF group lines → records → the same schema.
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        schema.Parameters[0].Description = " C:\\путь\\U+0041 ^J \\M+1A2B 😀\nстрока " + string.Concat(Enumerable.Repeat("длинный текст ", 40));
        schema.Extra.Add("x-число", JsonValue.Parse("0.30000000000000004"));
        var records = SchemaNod.ToRecords(schema, Stamp);
        var reopened = FromDxf(ToDxf(records));
        var read = SchemaNod.FromRecords(reopened);
        Assert.Empty(read.Issues);
        Assert.Equal(ParameterSchemaJson.Write(schema), ParameterSchemaJson.Write(read.Schema!));
        Assert.Equal(SchemaNod.ComputeHash(schema), read.Info!.Hash);
    }

    /// <summary>Writes values the way ASCII DXF does: one line per value, reals with 16 significant digits.</summary>
    private static string ToDxf(IReadOnlyList<DataRecord> records)
    {
        var text = new StringBuilder();
        foreach (var record in records)
        {
            text.Append("XRECORD\n").Append(record.Name).Append('\n');
            foreach (var value in record.Values)
            {
                var line = value.Value switch
                {
                    double number => number.ToString("G16", System.Globalization.CultureInfo.InvariantCulture),
                    bool flag => flag ? "1" : "0",
                    IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                    _ => (string)value.Value,
                };
                Assert.DoesNotContain('\n', line);
                text.Append(value.TypeCode).Append('\n').Append(line).Append('\n');
            }
        }
        return text.ToString();
    }

    /// <summary>Reads lines back like a lenient DXF reader: trims values, decodes <c>\U+XXXX</c> and <c>^</c> sequences.</summary>
    private static List<DataRecord> FromDxf(string dxf)
    {
        var lines = dxf.Split('\n');
        var records = new List<DataRecord>();
        List<DataValue>? values = null;
        string? name = null;
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            if (lines[i] == "XRECORD")
            {
                if (name != null) records.Add(new DataRecord(name, values!));
                name = lines[i + 1]; values = new List<DataValue>();
                continue;
            }
            var code = int.Parse(lines[i], System.Globalization.CultureInfo.InvariantCulture);
            var raw = System.Text.RegularExpressions.Regex.Replace(lines[i + 1].Trim(), @"\\U\+([0-9A-Fa-f]{4})",
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\^(.)", match => match.Groups[1].Value == " " ? "^" : ((char)(match.Groups[1].Value[0] - '@')).ToString());
            object parsed = code switch
            {
                40 => double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                90 => int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                290 => short.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                _ => raw,
            };
            values!.Add(new DataValue(code, parsed));
        }
        if (name != null) records.Add(new DataRecord(name, values!));
        return records;
    }

    [Fact]
    public void LongTextIsSplitWithoutBreakingSurrogatePairsOrTrimmableSpaces()
    {
        var text = string.Concat(Enumerable.Repeat("Описание 😀 параметра ", 60));
        var schema = Schema("{ 'id': 'P', 'type': 'String', 'caption': 'c' }");
        schema.Parameters[0].Description = text.Trim();
        schema.Parameters[0].Format = string.Concat(Enumerable.Repeat("@@[P]\\", 100));
        var records = SchemaNod.ToRecords(schema, Stamp);
        AssertDxfSafe(records);
        Assert.True(records[1].Values.Count(value => value.TypeCode == 300) > 5);
        Assert.True(records[1].Values.Count(value => value.TypeCode == 302) > 2);
        var back = SchemaNod.FromRecords(records).Schema!.Parameters[0];
        Assert.Equal(text.Trim(), back.Description);
        Assert.Equal(schema.Parameters[0].Format, back.Format);
    }

    [Fact]
    public void UnknownFieldsAndUnusualNamesRoundTrip()
    {
        var longName = new string('Ж', 200);
        var json = "{ 'format': 'BobrovXDATA.schema', 'version': 1, 'author': 'Бобров', 'meta': { 'a': [1, 2.50, null] }," +
            " 'parameters': [ { 'id': 'P', 'type': 'String', 'caption': 'c', 'x-new': true, 'имя с пробелом': 1, '': 'пусто', '" + longName + "': 'длинное' } ]," +
            " 'enums': [], 'templates': [] }";
        var schema = ParameterSchemaJson.Read(json.Replace('\'', '"')).Schema!;
        var records = SchemaNod.ToRecords(schema, Stamp);
        AssertDxfSafe(records);
        Assert.Contains(records[1].Values, value => value.TypeCode == 303);
        Assert.Contains(records[0].Values, value => value.TypeCode == 1 && (string)value.Value == "author");
        var read = SchemaNod.FromRecords(records);
        Assert.DoesNotContain(read.Issues, issue => issue.Code == SchemaIssueCode.InvalidStorage);
        Assert.Equal(ParameterSchemaJson.Write(schema), ParameterSchemaJson.Write(read.Schema!));
    }

    [Fact]
    public void ValuesAsReturnedByAutoCadAreAccepted()
    {
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        // AutoCAD returns 290 as short/int and keeps dictionary keys in any case.
        var records = SchemaNod.ToRecords(schema, Stamp).Select(record => new DataRecord(record.Name.ToLowerInvariant() == "info" ? "info" : record.Name,
            record.Values.Select(value => value.Value is bool flag ? new DataValue(value.TypeCode, (short)(flag ? 1 : 0)) :
                value.TypeCode == 90 ? new DataValue(90, (long)(int)value.Value) : value).ToList())).Reverse().ToList();
        var read = SchemaNod.FromRecords(records);
        Assert.Empty(read.Issues);
        Assert.Equal(ParameterSchemaJson.Write(schema), ParameterSchemaJson.Write(read.Schema!));
    }

    [Fact]
    public void NoRecordsMeansNoSchema()
    {
        var read = SchemaNod.FromRecords(Array.Empty<DataRecord>());
        Assert.False(read.Exists);
        Assert.Empty(read.Issues);
        Assert.Null(read.Info);
    }

    [Fact]
    public void DamagedValueIsReportedAndTheRestIsKept()
    {
        var records = SchemaNod.ToRecords(ParameterSchemaJson.LoadFile(ExamplePath).Schema!, Stamp).ToList();
        var power = records.FindIndex(record => record.Name == "PARAMETERS/Power");
        var values = records[power].Values.ToList();
        var editor = values.FindIndex(value => value.TypeCode == 1 && (string)value.Value == "editor");
        values[editor + 1] = new DataValue(302, "{\"unit\":");
        records[power] = new DataRecord(records[power].Name, values);

        var read = SchemaNod.FromRecords(records);
        Assert.True(read.IsDamaged);
        var issue = Assert.Single(read.Issues, i => i.Code == SchemaIssueCode.InvalidStorage && i.Severity == SchemaIssueSeverity.Error);
        Assert.Equal(("Параметр Power", "editor"), (issue.Object, issue.Field));
        var parameter = read.Schema!.FindParameter("Power")!;
        Assert.Null(parameter.Editor);
        Assert.Equal("Double", parameter.Type);
        Assert.Contains(read.Issues, i => i.Severity == SchemaIssueSeverity.Warning && i.Message.Contains("контрольная сумма"));
    }

    [Fact]
    public void StructuralProblemsAreReported()
    {
        var schema = Schema("{ 'id': 'P', 'type': 'String', 'caption': 'c' }");
        var records = SchemaNod.ToRecords(schema, Stamp).ToList();

        var noInfo = SchemaNod.FromRecords(records.Skip(1).ToList());
        Assert.True(noInfo.IsDamaged);
        Assert.Contains(noInfo.Issues, i => i.Field == "INFO" && i.Severity == SchemaIssueSeverity.Error);

        var foreign = records.Concat(new[] { new DataRecord("OTHER/X", new[] { new DataValue(1, "x") }) }).ToList();
        var foreignRead = SchemaNod.FromRecords(foreign, new[] { "превышен лимит" });
        Assert.Contains(foreignRead.Issues, i => i.Object == "VELEC/OTHER/X" && i.Severity == SchemaIssueSeverity.Warning);
        Assert.Contains(foreignRead.Issues, i => i.Message.Contains("превышен лимит") && i.Severity == SchemaIssueSeverity.Error);

        var renamed = records.Select(r => r.Name == "PARAMETERS/P" ? new DataRecord("PARAMETERS/Q", r.Values) : r).ToList();
        Assert.Contains(SchemaNod.FromRecords(renamed).Issues, i => i.Field == "id" && i.Severity == SchemaIssueSeverity.Warning);

        var info = records[0].Values.ToList();
        info[1] = new DataValue(90, 2);
        var newer = SchemaNod.FromRecords(new[] { new DataRecord("INFO", info) }.Concat(records.Skip(1)).ToList());
        Assert.False(newer.IsDamaged);
        Assert.Equal(2, newer.Info!.StorageVersion);
        Assert.Contains(newer.Issues, i => i.Message.Contains("новее") && i.Severity == SchemaIssueSeverity.Warning);

        var duplicate = records[1].Values.Concat(new[] { new DataValue(1, "caption"), new DataValue(300, "второй") }).ToList();
        var duplicateRead = SchemaNod.FromRecords(new[] { records[0], new DataRecord("PARAMETERS/P", duplicate) });
        Assert.Equal("c", duplicateRead.Schema!.Parameters[0].Caption);
        Assert.Contains(duplicateRead.Issues, i => i.Field == "caption" && i.Severity == SchemaIssueSeverity.Error);

        var orphan = records[1].Values.Concat(new[] { new DataValue(1, "lost") }).Concat(new[] { new DataValue(70, (short)1) }).ToList();
        var orphanRead = SchemaNod.FromRecords(new[] { records[0], new DataRecord("PARAMETERS/P", orphan) });
        Assert.Equal(2, orphanRead.Issues.Count(i => i.Severity == SchemaIssueSeverity.Error));
        Assert.Equal("c", orphanRead.Schema!.Parameters[0].Caption);
    }

    [Fact]
    public void ElementsKeepTheirOrderFromTheOrderField()
    {
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        var records = SchemaNod.ToRecords(schema, Stamp).OrderBy(record => record.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(schema.Parameters.Select(p => p.Id), SchemaNod.FromRecords(records).Schema!.Parameters.Select(p => p.Id));
    }

    [Fact]
    public void InvalidIdsAreNotWritten()
    {
        var schema = Schema("{ 'id': 'P', 'type': 'String', 'caption': 'c' }");
        schema.Parameters[0].Id = "A/B";
        Assert.Throws<InvalidOperationException>(() => SchemaNod.ToRecords(schema, Stamp));
    }

    [Fact]
    public void PathsSplitIntoSectionAndId()
    {
        Assert.Equal(("PARAMETERS", "Power"), SchemaNod.SplitPath("parameters/Power"));
        Assert.Null(SchemaNod.SplitPath("INFO"));
        Assert.Null(SchemaNod.SplitPath("OTHER/X"));
        Assert.Null(SchemaNod.SplitPath("PARAMETERS/"));
        Assert.Null(SchemaNod.SplitPath("PARAMETERS/A/B"));
    }

    internal static ParameterSchema Schema(string parameters, string enums = "", string templates = "")
    {
        var json = "{ 'format': 'BobrovXDATA.schema', 'version': 1, 'parameters': [" + parameters + "], 'enums': [" + enums + "], 'templates': [" + templates + "] }";
        var result = ParameterSchemaJson.Read(json.Replace('\'', '"'));
        Assert.DoesNotContain(result.Issues, issue => issue.Severity == SchemaIssueSeverity.Error);
        return result.Schema!;
    }

    private static Dictionary<string, (int Code, object Value)> Fields(DataRecord record)
    {
        var fields = new Dictionary<string, (int, object)>();
        for (var i = 0; i < record.Values.Count; i++)
            if (record.Values[i].TypeCode == 1)
            {
                var code = record.Values[i + 1].TypeCode;
                var value = code is 300 or 302 ? string.Concat(record.Values.Skip(i + 1).TakeWhile(v => v.TypeCode == code).Select(v => (string)v.Value)) : record.Values[i + 1].Value;
                fields[(string)record.Values[i].Value] = (code, value);
            }
        return fields;
    }

    /// <summary>Codes allowed in an Xrecord; strings of at most 240 bytes that DXF reads back unchanged.</summary>
    internal static void AssertDxfSafe(IEnumerable<DataRecord> records)
    {
        foreach (var value in records.SelectMany(record => record.Values))
        {
            Assert.Contains(value.TypeCode, AllowedCodes);
            if (!(value.Value is string text)) continue;
            Assert.True(Encoding.UTF8.GetByteCount(text) <= DwgText.MaxFragmentBytes, text);
            // Backslashes only come from JSON escapes (codes 302/303), never as DXF sequences \U+XXXX, \M+NXXXX.
            if (value.TypeCode != 302 && value.TypeCode != 303) Assert.DoesNotContain('\\', text);
            Assert.DoesNotContain("\\U+", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\M+", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('^', text);
            Assert.DoesNotContain(text, c => c < ' ');
            if (text.Length != 0) Assert.False(char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]), "[" + text + "]");
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i])) { Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), text); i++; }
                else Assert.False(char.IsLowSurrogate(text[i]), text);
            }
        }
    }
}
