using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

public sealed class SchemaLoadResult
{
    public SchemaLoadResult(ParameterSchema? schema, IReadOnlyList<SchemaIssue> issues) { Schema = schema; Issues = issues; }
    /// <summary>Null only when the text is not a JSON object.</summary>
    public ParameterSchema? Schema { get; }
    public IReadOnlyList<SchemaIssue> Issues { get; }
    public bool HasErrors => Issues.Any(issue => issue.Severity == SchemaIssueSeverity.Error);
}

/// <summary>
/// <c>schema.json</c> reader/writer (TZXDATA 5.1) on the built-in <see cref="JsonValue"/> DOM.
/// Known fields of a wrong JSON type are reported and kept in <c>Extra</c>, so nothing is lost.
/// Output is deterministic: known fields in documented order, then unknown fields in source order.
/// </summary>
public static class ParameterSchemaJson
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly string[] RootFields = { "format", "version", "parameters", "enums", "templates" };
    private static readonly string[] ParameterFields =
        { "id", "type", "caption", "description", "group", "default", "enum", "format", "persist", "columns", "groupBy", "key", "editor" };
    private static readonly string[] EditorFields =
        { "unit", "precision", "min", "max", "maxLength", "readOnly", "multiline", "allowCustom", "vstColumn" };
    private static readonly string[] EnumFields = { "id", "caption", "items" };
    private static readonly string[] EnumItemFields = { "key", "caption" };
    private static readonly string[] TemplateFields = { "id", "caption", "entityTypes", "parameters" };
    private static readonly string[] TemplateParameterFields = { "id", "default" };

    /// <summary>Reads and validates (TZXDATA 4.5) a schema.</summary>
    public static SchemaLoadResult Load(string json, ParameterTypeRegistry? types = null)
    {
        var read = Read(json);
        if (read.Schema is null) return read;
        return new SchemaLoadResult(read.Schema, read.Issues.Concat(new SchemaValidator(types).Validate(read.Schema)).ToList());
    }

    public static SchemaLoadResult LoadFile(string path, ParameterTypeRegistry? types = null) =>
        Load(File.ReadAllText(path, Utf8), types);

    public static void WriteFile(string path, ParameterSchema schema) => File.WriteAllText(path, Write(schema), Utf8);

    /// <summary>Maps JSON to the model; reports syntax and field type problems only.</summary>
    public static SchemaLoadResult Read(string json)
    {
        var issues = new List<SchemaIssue>();
        JsonValue root;
        try { root = JsonValue.Parse(json); }
        catch (JsonException error)
        {
            issues.Add(new SchemaIssue(SchemaIssueCode.InvalidJson, SchemaIssue.SchemaObject, "", error.Message));
            return new SchemaLoadResult(null, issues);
        }
        if (!(root is JsonObject obj))
        {
            issues.Add(new SchemaIssue(SchemaIssueCode.InvalidJson, SchemaIssue.SchemaObject, "", "корень JSON должен быть объектом"));
            return new SchemaLoadResult(null, issues);
        }
        var reader = new FieldReader(issues);
        var schema = new ParameterSchema { Format = null, Version = null };
        reader.Fields(obj, schema, SchemaIssue.SchemaObject, RootFields, (name, value) =>
        {
            switch (name)
            {
                case "format": return reader.String(value, s => schema.Format = s);
                case "version": return reader.Int(value, v => schema.Version = v);
                case "parameters": return reader.Objects(value, schema.Parameters, (item, path) => ReadParameter(reader, item, path), "Параметр");
                case "enums": return reader.Objects(value, schema.Enums, (item, path) => ReadEnum(reader, item, path), "Справочник");
                case "templates": return reader.Objects(value, schema.Templates, (item, path) => ReadTemplate(reader, item, path), "Шаблон");
                default: return null;
            }
        });
        return new SchemaLoadResult(schema, issues);
    }

    public static string Write(ParameterSchema schema) => ToJson(schema).ToJson() + "\n";

    public static JsonObject ToJson(ParameterSchema schema)
    {
        var result = new JsonObject();
        Put(result, "format", schema.Format);
        if (schema.Version.HasValue) result.Add("version", new JsonNumber(schema.Version.Value));
        List(result, schema, "parameters", schema.Parameters.Select(ToJson));
        List(result, schema, "enums", schema.Enums.Select(ToJson));
        List(result, schema, "templates", schema.Templates.Select(ToJson));
        return AddExtra(result, schema);
    }

    private static JsonObject ToJson(SchemaParameter parameter)
    {
        var result = new JsonObject();
        Put(result, "id", parameter.Id);
        Put(result, "type", parameter.Type);
        Put(result, "caption", parameter.Caption);
        Put(result, "description", parameter.Description);
        Put(result, "group", parameter.Group);
        if (parameter.Default != null) result.Add("default", parameter.Default);
        Put(result, "enum", parameter.Enum);
        Put(result, "format", parameter.Format);
        if (parameter.Persist.HasValue) result.Add("persist", JsonBoolean.From(parameter.Persist.Value));
        if (parameter.Columns != null) result.Add("columns", Strings(parameter.Columns));
        Put(result, "groupBy", parameter.GroupBy);
        Put(result, "key", parameter.Key);
        if (parameter.Editor != null) result.Add("editor", ToJson(parameter.Editor));
        return AddExtra(result, parameter);
    }

    private static JsonObject ToJson(ParameterEditor editor)
    {
        var result = new JsonObject();
        Put(result, "unit", editor.Unit);
        if (editor.Precision.HasValue) result.Add("precision", new JsonNumber(editor.Precision.Value));
        if (editor.Min.HasValue) result.Add("min", new JsonNumber(editor.Min.Value));
        if (editor.Max.HasValue) result.Add("max", new JsonNumber(editor.Max.Value));
        if (editor.MaxLength.HasValue) result.Add("maxLength", new JsonNumber(editor.MaxLength.Value));
        if (editor.ReadOnly.HasValue) result.Add("readOnly", JsonBoolean.From(editor.ReadOnly.Value));
        if (editor.Multiline.HasValue) result.Add("multiline", JsonBoolean.From(editor.Multiline.Value));
        if (editor.AllowCustom.HasValue) result.Add("allowCustom", JsonBoolean.From(editor.AllowCustom.Value));
        if (editor.VstColumn.HasValue) result.Add("vstColumn", new JsonNumber(editor.VstColumn.Value));
        return AddExtra(result, editor);
    }

    private static JsonObject ToJson(SchemaEnum item)
    {
        var result = new JsonObject();
        Put(result, "id", item.Id);
        Put(result, "caption", item.Caption);
        List(result, item, "items", item.Items.Select(entry =>
        {
            var value = new JsonObject();
            Put(value, "key", entry.Key);
            Put(value, "caption", entry.Caption);
            return (JsonValue)AddExtra(value, entry);
        }));
        return AddExtra(result, item);
    }

    private static JsonObject ToJson(SchemaTemplate template)
    {
        var result = new JsonObject();
        Put(result, "id", template.Id);
        Put(result, "caption", template.Caption);
        if (template.EntityTypes != null) result.Add("entityTypes", Strings(template.EntityTypes));
        List(result, template, "parameters", template.Parameters.Select(parameter =>
        {
            // A plain reference is written as a string, as in the TZXDATA 5.1 example.
            if (parameter.Default is null && parameter.Extra.Count == 0 && parameter.Id != null) return (JsonValue)new JsonString(parameter.Id);
            var value = new JsonObject();
            Put(value, "id", parameter.Id);
            if (parameter.Default != null) value.Add("default", parameter.Default);
            return AddExtra(value, parameter);
        }));
        return AddExtra(result, template);
    }

    private static SchemaParameter ReadParameter(FieldReader reader, JsonObject obj, string path)
    {
        var parameter = new SchemaParameter();
        path = reader.Path(obj, path);
        reader.Fields(obj, parameter, path, ParameterFields, (name, value) =>
        {
            switch (name)
            {
                case "id": return reader.String(value, s => parameter.Id = s);
                case "type": return reader.String(value, s => parameter.Type = s);
                case "caption": return reader.String(value, s => parameter.Caption = s);
                case "description": return reader.String(value, s => parameter.Description = s);
                case "group": return reader.String(value, s => parameter.Group = s);
                case "default": parameter.Default = value; return "";
                case "enum": return reader.String(value, s => parameter.Enum = s);
                case "format": return reader.String(value, s => parameter.Format = s);
                case "persist": return reader.Bool(value, b => parameter.Persist = b);
                case "columns": return reader.Strings(value, list => parameter.Columns = list);
                case "groupBy": return reader.String(value, s => parameter.GroupBy = s);
                case "key": return reader.String(value, s => parameter.Key = s);
                case "editor":
                    if (!(value is JsonObject editor)) return "ожидается объект";
                    parameter.Editor = ReadEditor(reader, editor, path);
                    return "";
                default: return null;
            }
        });
        return parameter;
    }

    private static ParameterEditor ReadEditor(FieldReader reader, JsonObject obj, string path)
    {
        var editor = new ParameterEditor();
        reader.Fields(obj, editor, path, EditorFields, (name, value) =>
        {
            switch (name)
            {
                case "unit": return reader.String(value, s => editor.Unit = s);
                case "precision": return reader.Int(value, v => editor.Precision = v);
                case "min": return reader.Double(value, v => editor.Min = v);
                case "max": return reader.Double(value, v => editor.Max = v);
                case "maxLength": return reader.Int(value, v => editor.MaxLength = v);
                case "readOnly": return reader.Bool(value, b => editor.ReadOnly = b);
                case "multiline": return reader.Bool(value, b => editor.Multiline = b);
                case "allowCustom": return reader.Bool(value, b => editor.AllowCustom = b);
                case "vstColumn": return reader.Int(value, v => editor.VstColumn = v);
                default: return null;
            }
        }, "editor.");
        return editor;
    }

    private static SchemaEnum ReadEnum(FieldReader reader, JsonObject obj, string path)
    {
        var result = new SchemaEnum();
        path = reader.Path(obj, path);
        reader.Fields(obj, result, path, EnumFields, (name, value) =>
        {
            switch (name)
            {
                case "id": return reader.String(value, s => result.Id = s);
                case "caption": return reader.String(value, s => result.Caption = s);
                case "items":
                    return reader.Objects(value, result.Items, (item, itemPath) =>
                    {
                        var entry = new SchemaEnumItem();
                        reader.Fields(item, entry, itemPath, EnumItemFields, (field, fieldValue) =>
                            field == "key" ? reader.String(fieldValue, s => entry.Key = s) :
                            field == "caption" ? reader.String(fieldValue, s => entry.Caption = s) : null);
                        return entry;
                    }, path + " / элемент");
                default: return null;
            }
        });
        return result;
    }

    private static SchemaTemplate ReadTemplate(FieldReader reader, JsonObject obj, string path)
    {
        var template = new SchemaTemplate();
        path = reader.Path(obj, path);
        reader.Fields(obj, template, path, TemplateFields, (name, value) =>
        {
            switch (name)
            {
                case "id": return reader.String(value, s => template.Id = s);
                case "caption": return reader.String(value, s => template.Caption = s);
                case "entityTypes": return reader.Strings(value, list => template.EntityTypes = list);
                case "parameters":
                    if (!(value is JsonArray array)) return "ожидается массив";
                    // Validated before mapping so that an invalid list is preserved as a whole in Extra.
                    if (array.Any(item => !(item is JsonString) && !(item is JsonObject))) return "ожидается массив строк или объектов";
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonString id) { template.Parameters.Add(new TemplateParameter(id.Value)); continue; }
                        var parameter = new TemplateParameter();
                        reader.Fields((JsonObject)array[i], parameter, path + " / параметр #" + (i + 1).ToString(CultureInfo.InvariantCulture), TemplateParameterFields, (field, fieldValue) =>
                        {
                            if (field == "id") return reader.String(fieldValue, s => parameter.Id = s);
                            if (field == "default") { parameter.Default = fieldValue; return ""; }
                            return null;
                        });
                        template.Parameters.Add(parameter);
                    }
                    return "";
                default: return null;
            }
        });
        return template;
    }

    private static void Put(JsonObject target, string name, string? value)
    {
        if (value != null) target.Add(name, new JsonString(value));
    }

    /// <summary>Lists are always written, unless empty because the source value was invalid and is kept in Extra.</summary>
    private static void List(JsonObject target, SchemaElement element, string name, IEnumerable<JsonValue> items)
    {
        var array = new JsonArray(items);
        if (array.Count != 0 || !element.Extra.ContainsKey(name)) target.Add(name, array);
    }

    private static JsonArray Strings(IEnumerable<string> values) => new JsonArray(values.Select(value => (JsonValue)new JsonString(value)));

    private static JsonObject AddExtra(JsonObject target, SchemaElement element)
    {
        foreach (var property in element.Extra)
            if (!target.ContainsKey(property.Key)) target.Add(property.Key, property.Value);
        return target;
    }

    /// <summary>Field callbacks return null for an unknown field, "" when read, or the reason of a type mismatch.</summary>
    private sealed class FieldReader
    {
        private readonly List<SchemaIssue> issues;
        public FieldReader(List<SchemaIssue> issues) { this.issues = issues; }

        public void Fields(JsonObject obj, SchemaElement target, string path, string[] known, Func<string, JsonValue, string?> read, string fieldPrefix = "")
        {
            foreach (var property in obj)
            {
                var problem = read(property.Key, property.Value);
                if (problem == "") continue;
                target.Extra.Add(property.Key, property.Value);
                if (problem != null)
                    issues.Add(new SchemaIssue(SchemaIssueCode.InvalidFieldType, path, fieldPrefix + property.Key, problem + "; значение сохранено без изменений"));
                else if (KnownName(property.Key, known) is string expected)
                    issues.Add(new SchemaIssue(SchemaIssueCode.FieldNameCase, path, fieldPrefix + property.Key,
                        "имена полей чувствительны к регистру, ожидается \"" + expected + "\"; поле сохранено как неизвестное", SchemaIssueSeverity.Warning));
            }
        }

        /// <summary>Path such as «Параметр Power» or «Параметр #3» when the id is missing.</summary>
        public string Path(JsonObject obj, string fallback) =>
            obj.TryGetValue("id", out var id) && id is JsonString text && text.Value.Length != 0 ? fallback.Substring(0, fallback.LastIndexOf(' ')) + " " + text.Value : fallback;

        public string? Objects<T>(JsonValue value, List<T> target, Func<JsonObject, string, T> map, string kind)
        {
            if (!(value is JsonArray array)) return "ожидается массив";
            if (array.Any(item => !(item is JsonObject))) return "ожидается массив объектов";
            for (var i = 0; i < array.Count; i++)
                target.Add(map((JsonObject)array[i], kind + " #" + (i + 1).ToString(CultureInfo.InvariantCulture)));
            return "";
        }

        public string? String(JsonValue value, Action<string> set)
        {
            if (!(value is JsonString text)) return "ожидается строка";
            set(text.Value);
            return "";
        }

        public string? Strings(JsonValue value, Action<List<string>> set)
        {
            if (!(value is JsonArray array) || array.Any(item => !(item is JsonString))) return "ожидается массив строк";
            set(array.Select(item => ((JsonString)item).Value).ToList());
            return "";
        }

        public string? Bool(JsonValue value, Action<bool> set)
        {
            if (!(value is JsonBoolean flag)) return "ожидается true или false";
            set(flag.Value);
            return "";
        }

        public string? Int(JsonValue value, Action<int> set)
        {
            if (!(value is JsonNumber number) || !number.TryGetInt32(out var result)) return "ожидается целое число";
            set(result);
            return "";
        }

        public string? Double(JsonValue value, Action<double> set)
        {
            if (!(value is JsonNumber number)) return "ожидается число";
            var result = number.ToDouble();
            if (double.IsInfinity(result)) return "число вне диапазона Double";
            set(result);
            return "";
        }

        /// <summary>A known field of this object whose name differs only by case (<c>Id</c> instead of <c>id</c>).</summary>
        private static string? KnownName(string name, IEnumerable<string> known) =>
            known.FirstOrDefault(field => field != name && string.Equals(field, name, StringComparison.OrdinalIgnoreCase));
    }
}
