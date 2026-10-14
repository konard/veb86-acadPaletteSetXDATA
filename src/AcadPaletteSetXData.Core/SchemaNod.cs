using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>Import stamp and integrity data of the <c>INFO</c> record.</summary>
public sealed class SchemaNodInfo
{
    public SchemaNodInfo(string? importedAt = null, string? source = null, string? mode = null)
    {
        ImportedAt = importedAt; Source = source; Mode = mode;
    }
    /// <summary>ISO 8601 UTC time of the import.</summary>
    public string? ImportedAt { get; }
    /// <summary>Path of the imported <c>schema.json</c>.</summary>
    public string? Source { get; }
    /// <summary><c>Merge</c> or <c>Replace</c>.</summary>
    public string? Mode { get; }
    /// <summary>Storage format version; filled when read, always <see cref="SchemaNod.StorageVersion"/> when written.</summary>
    public int? StorageVersion { get; internal set; }
    /// <summary>SHA-256 of the canonical JSON; filled when read, always recomputed when written.</summary>
    public string? Hash { get; internal set; }

    public static SchemaNodInfo Now(string? source, SchemaImportMode mode) =>
        new SchemaNodInfo(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), source, mode.ToString());
}

public sealed class SchemaNodReadResult
{
    public SchemaNodReadResult(ParameterSchema? schema, SchemaNodInfo? info, IReadOnlyList<SchemaIssue> issues)
    {
        Schema = schema; Info = info; Issues = issues;
    }
    /// <summary>Null when the drawing has no schema records.</summary>
    public ParameterSchema? Schema { get; }
    public SchemaNodInfo? Info { get; }
    public IReadOnlyList<SchemaIssue> Issues { get; }
    public bool Exists => Schema != null;
    /// <summary>Records could not be decoded completely; the schema is partial.</summary>
    public bool IsDamaged => Issues.Any(issue => issue.Code == SchemaIssueCode.InvalidStorage && issue.Severity == SchemaIssueSeverity.Error);
}

/// <summary>
/// Schema in the drawing (TZXDATA 5.2): records under the NOD dictionary <c>VELEC</c>, one Xrecord per schema object.
/// Records are detached <see cref="DataRecord"/>s whose names are paths relative to <c>VELEC</c>
/// (<c>INFO</c>, <c>PARAMETERS/Power</c>), so the format is tested without AutoCAD.
/// </summary>
/// <remarks>
/// Every Xrecord is a list of fields: a name (<c>1</c>, or <c>303</c> fragments of a JSON string for unusual names;
/// <c>2</c> for system fields such as <c>order</c>) followed by one value: <c>300</c> text fragments,
/// <c>290</c> boolean, <c>90</c> integer, <c>40</c> real, or <c>302</c> fragments of compact JSON for
/// lists, objects, null, numbers without an exact binary form and text DXF could alter.
/// Fields are those of the canonical <c>schema.json</c> object, so new and unknown fields round-trip as is.
/// </remarks>
public static class SchemaNod
{
    public const string InfoName = "INFO";
    public const string ParametersName = "PARAMETERS";
    public const string EnumsName = "ENUMS";
    public const string TemplatesName = "TEMPLATES";
    public const int StorageVersion = 1;

    public const int NameCode = 1;
    public const int SystemNameCode = 2;
    public const int DoubleCode = 40;
    public const int IntegerCode = 90;
    public const int BooleanCode = 290;
    public const int TextCode = 300;
    public const int JsonCode = 302;
    public const int NameJsonCode = 303;

    private const string OrderField = "order";
    private const string StorageField = "storage";
    private const string HashField = "hash";
    private const string ImportedAtField = "importedAt";
    private const string SourceField = "source";
    private const string ModeField = "mode";
    /// <summary>INFO fields that describe the import, not the schema.</summary>
    internal static readonly string[] StampFields = { ImportedAtField, SourceField, ModeField };
    private static readonly string[] Sections = { ParametersName, EnumsName, TemplatesName };
    private static readonly string[] ListFields = { "parameters", "enums", "templates" };
    private static readonly string[] SectionKinds = { "Параметр", "Справочник", "Шаблон" };

    public static IReadOnlyList<string> SectionNames => Sections;

    public static string PathOf(string section, string id) => section + "/" + id;

    /// <summary>Section name and Id of an element record path, or null for INFO and foreign records.</summary>
    public static (string Section, string Id)? SplitPath(string path)
    {
        var slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1 || path.IndexOf('/', slash + 1) >= 0) return null;
        var section = Sections.FirstOrDefault(name => string.Equals(name, path.Substring(0, slash), StringComparison.OrdinalIgnoreCase));
        return section is null ? ((string, string)?)null : (section, path.Substring(slash + 1));
    }

    /// <summary>SHA-256 (hex) of the canonical <c>schema.json</c> text.</summary>
    public static string ComputeHash(ParameterSchema schema)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(ParameterSchemaJson.Write(schema)));
        return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>INFO first, then sections in order. Elements need valid unique Ids (check with <see cref="SchemaValidator"/>).</summary>
    public static IReadOnlyList<DataRecord> ToRecords(ParameterSchema schema, SchemaNodInfo info)
    {
        var root = ParameterSchemaJson.ToJson(schema);
        var records = new List<DataRecord> { InfoRecord(schema, root, info) };
        AddSection(records, ParametersName, schema.Parameters.Select(p => (p.Id, ParameterSchemaJson.ToJson(p))));
        AddSection(records, EnumsName, schema.Enums.Select(e => (e.Id, ParameterSchemaJson.ToJson(e))));
        AddSection(records, TemplatesName, schema.Templates.Select(t => (t.Id, ParameterSchemaJson.ToJson(t))));
        return records;
    }

    private static DataRecord InfoRecord(ParameterSchema schema, JsonObject root, SchemaNodInfo info)
    {
        var values = new List<DataValue>();
        AddField(values, StorageField, new JsonNumber(StorageVersion), system: true);
        AddField(values, HashField, new JsonString(ComputeHash(schema)), system: true);
        if (info.ImportedAt != null) AddField(values, ImportedAtField, new JsonString(info.ImportedAt), system: true);
        if (info.Source != null) AddField(values, SourceField, new JsonString(info.Source), system: true);
        if (info.Mode != null) AddField(values, ModeField, new JsonString(info.Mode), system: true);
        foreach (var property in root)
            if (!(ListFields.Contains(property.Key) && property.Value is JsonArray)) AddField(values, property.Key, property.Value, system: false);
        return new DataRecord(InfoName, values);
    }

    private static void AddSection(List<DataRecord> records, string section, IEnumerable<(string? Id, JsonObject Json)> elements)
    {
        var order = 0;
        foreach (var (id, json) in elements)
        {
            if (!SchemaValidator.IsValidId(id)) throw new InvalidOperationException("Недопустимый Id объекта схемы: " + (id ?? "(нет)"));
            var values = new List<DataValue>();
            AddField(values, OrderField, new JsonNumber(order++), system: true);
            foreach (var property in json) AddField(values, property.Key, property.Value, system: false);
            records.Add(new DataRecord(PathOf(section, id!), values));
        }
    }

    private static void AddField(List<DataValue> values, string name, JsonValue value, bool system)
    {
        if (system) values.Add(new DataValue(SystemNameCode, name));
        else if (IsPlainText(name) && name.Length != 0 && Encoding.UTF8.GetByteCount(name) <= DwgText.MaxFragmentBytes) values.Add(new DataValue(NameCode, name));
        else values.AddRange(Fragments(NameJsonCode, new JsonString(name).ToCompactJson()));

        switch (value)
        {
            case JsonString text when IsPlainText(text.Value): values.AddRange(Fragments(TextCode, text.Value)); break;
            case JsonBoolean flag: values.Add(new DataValue(BooleanCode, flag.Value)); break;
            case JsonNumber number when TryInt32Text(number.Text, out var integer): values.Add(new DataValue(IntegerCode, integer)); break;
            case JsonNumber number when TryExactDouble(number.Text, out var real): values.Add(new DataValue(DoubleCode, real)); break;
            default: values.AddRange(Fragments(JsonCode, value.ToCompactJson())); break;
        }
    }

    private static IEnumerable<DataValue> Fragments(int code, string text) =>
        DwgText.Split(text, keepWhitespaceInside: true).Select(fragment => new DataValue(code, fragment));

    /// <summary>
    /// Text stored as is: no control characters or lone surrogates (not valid in DXF), no <c>\</c> or <c>^</c>
    /// (DXF escape sequences <c>\U+XXXX</c>, <c>^J</c>) and no whitespace at the ends.
    /// </summary>
    private static bool IsPlainText(string text)
    {
        if (text.Length != 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]))) return false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < ' ' || c == '\\' || c == '^') return false;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c)) return false;
        }
        return true;
    }

    private static bool TryInt32Text(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) &&
        value.ToString(CultureInfo.InvariantCulture) == text;

    /// <summary>
    /// A real is stored as code 40 only when its JSON text is the shortest round-trip form with at most
    /// 15 significant digits: then .NET Framework and .NET 8 format the read value identically.
    /// </summary>
    private static bool TryExactDouble(string text, out double value)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            value == 0 || double.IsInfinity(value) || value.ToString("R", CultureInfo.InvariantCulture) != text) return false;
        var mantissa = text.TrimStart('-');
        var exponent = mantissa.IndexOfAny(new[] { 'e', 'E' });
        if (exponent >= 0) mantissa = mantissa.Substring(0, exponent);
        return mantissa.Replace(".", "").Trim('0').Length <= 15;
    }

    public static SchemaNodReadResult FromRecords(IReadOnlyList<DataRecord> records, IEnumerable<string>? readWarnings = null)
    {
        var issues = new List<SchemaIssue>();
        foreach (var warning in readWarnings ?? Enumerable.Empty<string>())
            issues.Add(Storage(SchemaIssue.SchemaObject, "", "чтение NOD: " + warning));

        DataRecord? infoRecord = null;
        var elements = Sections.Select(_ => new List<(int Order, int Index, JsonObject Json)>()).ToArray();
        var index = 0;
        foreach (var record in records)
        {
            index++;
            if (string.Equals(record.Name, InfoName, StringComparison.OrdinalIgnoreCase)) { infoRecord = record; continue; }
            var path = SplitPath(record.Name);
            if (path is null)
            {
                issues.Add(Storage(DrawingNames.NodRoot + "/" + record.Name, "", "неизвестная запись пропущена и сохраняется без изменений", SchemaIssueSeverity.Warning));
                continue;
            }
            var section = Array.IndexOf(Sections, path.Value.Section);
            var objectPath = SectionKinds[section] + " " + path.Value.Id;
            var fields = Decode(record, objectPath, issues);
            var json = new JsonObject();
            var order = int.MaxValue;
            foreach (var field in fields)
            {
                if (field.System)
                {
                    if (field.Name == OrderField && field.Value is JsonNumber number && number.TryGetInt32(out var position)) order = position;
                    continue;
                }
                json.Add(field.Name, field.Value);
            }
            if (!(json["id"] is JsonString id) || !string.Equals(id.Value, path.Value.Id, StringComparison.OrdinalIgnoreCase))
                issues.Add(Storage(objectPath, "id", "Id в записи не совпадает с ключом словаря", SchemaIssueSeverity.Warning));
            elements[section].Add((order, index, json));
        }

        if (infoRecord is null && elements.All(list => list.Count == 0))
            return new SchemaNodReadResult(null, null, issues);

        var root = new JsonObject();
        var info = new SchemaNodInfo();
        if (infoRecord is null) issues.Add(Storage(SchemaIssue.SchemaObject, InfoName, "нет записи INFO"));
        else
        {
            var values = new Dictionary<string, JsonValue>();
            foreach (var field in Decode(infoRecord, SchemaIssue.SchemaObject, issues))
            {
                if (field.System) values[field.Name] = field.Value;
                else if (!ListFields.Contains(field.Name)) root.Add(field.Name, field.Value);
                else issues.Add(Storage(SchemaIssue.SchemaObject, field.Name, "список в записи INFO пропущен"));
            }
            string? Text(string name) => values.TryGetValue(name, out var value) && value is JsonString text ? text.Value : null;
            info = new SchemaNodInfo(Text(ImportedAtField), Text(SourceField), Text(ModeField)) { Hash = Text(HashField) };
            if (values.TryGetValue(StorageField, out var storage) && storage is JsonNumber number && number.TryGetInt32(out var version))
            {
                info.StorageVersion = version;
                if (version > StorageVersion)
                    issues.Add(Storage(SchemaIssue.SchemaObject, InfoName, "формат хранения новее поддерживаемого (" +
                        StorageVersion.ToString(CultureInfo.InvariantCulture) + "); неизвестные поля сохраняются без изменений", SchemaIssueSeverity.Warning));
            }
            else issues.Add(Storage(SchemaIssue.SchemaObject, InfoName, "не задана версия формата хранения"));
        }
        for (var section = 0; section < Sections.Length; section++)
            root.Add(ListFields[section], new JsonArray(elements[section].OrderBy(e => e.Order).ThenBy(e => e.Index).Select(e => (JsonValue)e.Json)));

        var read = ParameterSchemaJson.Read(root);
        issues.AddRange(read.Issues);
        var schema = read.Schema!;
        if (info.Hash != null && info.Hash != ComputeHash(schema))
            issues.Add(Storage(SchemaIssue.SchemaObject, InfoName, "контрольная сумма не совпадает: схема изменена не командой импорта или повреждена", SchemaIssueSeverity.Warning));
        return new SchemaNodReadResult(schema, info, issues);
    }

    /// <summary>Decodes name/value pairs; a damaged pair is reported and skipped, the rest of the record is kept.</summary>
    private static List<(bool System, string Name, JsonValue Value)> Decode(DataRecord record, string objectPath, List<SchemaIssue> issues)
    {
        var fields = new List<(bool, string, JsonValue)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = record.Values;
        var i = 0;
        while (i < values.Count)
        {
            bool system;
            string? name;
            var code = values[i].TypeCode;
            if ((code == NameCode || code == SystemNameCode) && values[i].Value is string text) { system = code == SystemNameCode; name = text; i++; }
            else if (code == NameJsonCode)
            {
                system = false;
                name = ParseJson(Join(values, ref i, NameJsonCode)) is JsonString parsed ? parsed.Value : null;
                if (name is null) { issues.Add(Storage(objectPath, "", "повреждено имя поля (код 303)")); SkipValue(values, ref i); continue; }
            }
            else
            {
                issues.Add(Storage(objectPath, "", "ожидается имя поля, получен код " + code.ToString(CultureInfo.InvariantCulture)));
                i++;
                continue;
            }

            var value = ReadValue(values, ref i);
            if (value is null) { issues.Add(Storage(objectPath, name, "нет значения или неподдерживаемый код")); continue; }
            if (!names.Add((system ? "\u0002" : "") + name)) { issues.Add(Storage(objectPath, name, "поле повторяется; используется первое значение")); continue; }
            fields.Add((system, name, value));
        }
        return fields;
    }

    private static JsonValue? ReadValue(IReadOnlyList<DataValue> values, ref int i)
    {
        if (i >= values.Count) return null;
        var value = values[i];
        switch (value.TypeCode)
        {
            case TextCode: return new JsonString(Join(values, ref i, TextCode));
            case JsonCode: return ParseJson(Join(values, ref i, JsonCode));
            case BooleanCode:
                i++;
                if (value.Value is bool flag) return JsonBoolean.From(flag);
                return TryInteger(value.Value, out var number) ? JsonBoolean.From(number != 0) : null;
            case IntegerCode:
                i++;
                return TryInteger(value.Value, out var integer) ? new JsonNumber(integer) : null;
            case DoubleCode:
                i++;
                return value.Value is double real && !double.IsNaN(real) && !double.IsInfinity(real) ? new JsonNumber(real) : null;
            default:
                // Leave the unexpected value to be reported as a name.
                return null;
        }
    }

    private static void SkipValue(IReadOnlyList<DataValue> values, ref int i)
    {
        if (i < values.Count && values[i].TypeCode != NameCode && values[i].TypeCode != SystemNameCode && values[i].TypeCode != NameJsonCode)
            ReadValue(values, ref i);
    }

    private static string Join(IReadOnlyList<DataValue> values, ref int i, int code)
    {
        var builder = new StringBuilder();
        while (i < values.Count && values[i].TypeCode == code) builder.Append(values[i++].Value as string);
        return builder.ToString();
    }

    private static JsonValue? ParseJson(string text)
    {
        try { return JsonValue.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static bool TryInteger(object value, out long result)
    {
        switch (value)
        {
            case short s: result = s; return true;
            case int n: result = n; return true;
            case long l: result = l; return true;
            case byte b: result = b; return true;
            default: result = 0; return false;
        }
    }

    private static SchemaIssue Storage(string objectPath, string field, string message, SchemaIssueSeverity severity = SchemaIssueSeverity.Error) =>
        new SchemaIssue(SchemaIssueCode.InvalidStorage, objectPath, field, message, severity);
}
