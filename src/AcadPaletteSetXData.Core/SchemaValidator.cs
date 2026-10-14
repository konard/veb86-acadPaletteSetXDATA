using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcadPaletteSetXData.Core;

public enum SchemaIssueSeverity { Error, Warning }

public enum SchemaIssueCode
{
    InvalidJson,
    InvalidFieldType,
    FieldNameCase,
    UnsupportedFormat,
    UnsupportedVersion,
    Required,
    InvalidId,
    ReservedId,
    DuplicateId,
    UnknownType,
    UnknownReference,
    DuplicateReference,
    NotApplicable,
    InvalidDefault,
    InvalidEditor,
    InvalidFormat,
    CyclicFormat,
    InvalidColumn,
    InvalidValue,
    /// <summary>Schema records in the drawing (NOD) are damaged, incomplete or of a newer storage format.</summary>
    InvalidStorage
}

/// <summary>One problem as «объект — поле — причина» (TZXDATA 4.5).</summary>
public sealed class SchemaIssue
{
    public const string SchemaObject = "Схема";

    public SchemaIssue(SchemaIssueCode code, string objectPath, string field, string message, SchemaIssueSeverity severity = SchemaIssueSeverity.Error)
    {
        Code = code; Object = objectPath; Field = field; Message = message; Severity = severity;
    }
    public SchemaIssueCode Code { get; }
    public SchemaIssueSeverity Severity { get; }
    public string Object { get; }
    public string Field { get; }
    public string Message { get; }

    public override string ToString() =>
        (Severity == SchemaIssueSeverity.Warning ? "Предупреждение: " : "") + Object + " — " + (Field.Length == 0 ? "—" : Field) + " — " + Message;
}

/// <summary>
/// Schema checks shared by JSON import, NOD and the schema editor (TZXDATA 4.5). A schema with
/// errors must not be written to NOD; warnings (unknown types, inapplicable fields) do not block it.
/// </summary>
public sealed class SchemaValidator
{
    public const int MaxIdLength = 64;
    private static readonly Regex IdPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private readonly ParameterTypeRegistry types;

    public SchemaValidator(ParameterTypeRegistry? types = null) { this.types = types ?? ParameterTypeRegistry.Default; }

    public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

    public IReadOnlyList<SchemaIssue> Validate(ParameterSchema schema)
    {
        var issues = new List<SchemaIssue>();
        if (schema.Format is null) issues.Add(Error(SchemaIssueCode.Required, SchemaIssue.SchemaObject, "format", "не задан формат, ожидается \"" + ParameterSchema.FormatName + "\""));
        else if (schema.Format != ParameterSchema.FormatName)
            issues.Add(Error(SchemaIssueCode.UnsupportedFormat, SchemaIssue.SchemaObject, "format", "ожидается \"" + ParameterSchema.FormatName + "\""));
        if (schema.Version is null) issues.Add(Error(SchemaIssueCode.Required, SchemaIssue.SchemaObject, "version", "не задана версия схемы"));
        else if (schema.Version < 1) issues.Add(Error(SchemaIssueCode.UnsupportedVersion, SchemaIssue.SchemaObject, "version", "версия должна быть не меньше 1"));
        else if (schema.Version > ParameterSchema.CurrentVersion)
            issues.Add(Warning(SchemaIssueCode.UnsupportedVersion, SchemaIssue.SchemaObject, "version",
                "версия новее поддерживаемой (" + ParameterSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture) + "); неизвестные поля сохраняются без изменений"));

        CheckIds(schema.Parameters, parameter => parameter.Id, "Параметр", issues, reserved: true);
        CheckIds(schema.Enums, item => item.Id, "Справочник", issues, reserved: false);
        CheckIds(schema.Templates, template => template.Id, "Шаблон", issues, reserved: false);
        for (var i = 0; i < schema.Parameters.Count; i++) CheckParameter(schema, schema.Parameters[i], Name("Параметр", schema.Parameters[i].Id, i), issues);
        for (var i = 0; i < schema.Enums.Count; i++) CheckEnum(schema.Enums[i], Name("Справочник", schema.Enums[i].Id, i), issues);
        for (var i = 0; i < schema.Templates.Count; i++) CheckTemplate(schema, schema.Templates[i], Name("Шаблон", schema.Templates[i].Id, i), issues);
        CheckCycles(schema, issues);
        return issues;
    }

    private static void CheckIds<T>(IReadOnlyList<T> items, Func<T, string?> id, string kind, List<SchemaIssue> issues, bool reserved)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var value = id(items[i]);
            var name = Name(kind, value, i);
            if (string.IsNullOrEmpty(value)) { issues.Add(Error(SchemaIssueCode.Required, name, "id", "не задан id")); continue; }
            if (!IsValidId(value))
                issues.Add(Error(SchemaIssueCode.InvalidId, name, "id", "допустимы латиница, цифры и «_», первый символ не цифра, длина до " + MaxIdLength.ToString(CultureInfo.InvariantCulture)));
            if (reserved && ParameterSchema.ReservedIds.Any(item => ParameterSchema.SameId(item, value)))
                issues.Add(Error(SchemaIssueCode.ReservedId, name, "id", "имя зарезервировано для системного поля XDATA"));
            if (!seen.Add(value!)) issues.Add(Error(SchemaIssueCode.DuplicateId, name, "id", "id повторяется (регистр не учитывается)"));
        }
    }

    private void CheckParameter(ParameterSchema schema, SchemaParameter parameter, string name, List<SchemaIssue> issues)
    {
        if (string.IsNullOrEmpty(parameter.Caption)) issues.Add(Error(SchemaIssueCode.Required, name, "caption", "не задано отображаемое имя"));
        if (string.IsNullOrEmpty(parameter.Type)) { issues.Add(Error(SchemaIssueCode.Required, name, "type", "не задан тип")); return; }
        var type = types.Find(parameter.Type);
        if (type is null)
        {
            // A type of a newer plugin version is kept and shown as a read-only string (TZXDATA 11).
            issues.Add(Warning(SchemaIssueCode.UnknownType, name, "type", "неизвестный тип \"" + parameter.Type + "\"; параметр будет только для чтения"));
            return;
        }
        var kind = type.Kind;
        NotApplicable(parameter.Enum != null && kind != ParameterKind.Enum, name, "enum", "Enum", issues);
        NotApplicable(parameter.Format != null && kind != ParameterKind.CalculatedString, name, "format", "CalculatedString", issues);
        NotApplicable(parameter.Persist != null && kind != ParameterKind.CalculatedString, name, "persist", "CalculatedString", issues);
        NotApplicable(parameter.Columns != null && kind != ParameterKind.Table, name, "columns", "Table", issues);
        NotApplicable(parameter.GroupBy != null && kind != ParameterKind.Table, name, "groupBy", "Table", issues);
        NotApplicable(parameter.Key != null && kind != ParameterKind.Table, name, "key", "Table", issues);

        if (kind == ParameterKind.Enum)
        {
            if (string.IsNullOrEmpty(parameter.Enum)) issues.Add(Error(SchemaIssueCode.Required, name, "enum", "не задан справочник"));
            else if (schema.FindEnum(parameter.Enum) is null) issues.Add(Error(SchemaIssueCode.UnknownReference, name, "enum", "справочник \"" + parameter.Enum + "\" не найден"));
        }
        if (kind == ParameterKind.CalculatedString) CheckFormat(schema, parameter, name, issues);
        if (kind == ParameterKind.Table) CheckTable(schema, parameter, name, issues);
        if (parameter.Editor != null) CheckEditor(parameter.Editor, kind, name, issues);
        if (parameter.Default != null && type.CheckValue(parameter.Default, parameter, schema) is string problem)
            issues.Add(Error(SchemaIssueCode.InvalidDefault, name, "default", problem));
    }

    private void CheckFormat(ParameterSchema schema, SchemaParameter parameter, string name, List<SchemaIssue> issues)
    {
        if (string.IsNullOrEmpty(parameter.Format)) { issues.Add(Error(SchemaIssueCode.Required, name, "format", "не задана строка формата")); return; }
        var fragments = CalculatedFormat.Parse(parameter.Format!, out var error);
        if (error != null) issues.Add(Error(SchemaIssueCode.InvalidFormat, name, "format", error));
        foreach (var reference in fragments.Where(fragment => fragment.Reference != null))
        {
            var target = schema.FindParameter(reference.Reference);
            if (target is null)
                issues.Add(Error(SchemaIssueCode.UnknownReference, name, "format", "@@[" + reference.Reference + "]: параметр не найден"));
            else if (reference.UseKey && Kind(target) != ParameterKind.Enum)
                issues.Add(Error(SchemaIssueCode.InvalidFormat, name, "format", "@@[" + reference.Reference + ":key] допустимо только для Enum"));
            else if (Kind(target) == ParameterKind.Table)
                issues.Add(Error(SchemaIssueCode.InvalidFormat, name, "format", "@@[" + reference.Reference + "]: Table нельзя подставить в строку"));
        }
    }

    private void CheckTable(ParameterSchema schema, SchemaParameter parameter, string name, List<SchemaIssue> issues)
    {
        if (parameter.Columns is null || parameter.Columns.Count == 0) { issues.Add(Error(SchemaIssueCode.Required, name, "columns", "не заданы колонки таблицы")); return; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in parameter.Columns)
        {
            var column = schema.FindParameter(id);
            if (column is null) { issues.Add(Error(SchemaIssueCode.UnknownReference, name, "columns", "колонка \"" + id + "\": параметр не найден")); continue; }
            if (!seen.Add(id)) issues.Add(Error(SchemaIssueCode.DuplicateReference, name, "columns", "колонка \"" + id + "\" повторяется"));
            var type = types.Find(column.Type);
            if (type != null && !type.IsScalar)
                issues.Add(Error(SchemaIssueCode.InvalidColumn, name, "columns", "колонка \"" + id + "\": " + type.Name + " нельзя вложить в Table"));
            else if (type?.Kind == ParameterKind.CalculatedString && column.Persist == true)
                issues.Add(Error(SchemaIssueCode.InvalidColumn, name, "columns", "колонка \"" + id + "\": CalculatedString с persist нельзя вложить в Table"));
        }
        if (parameter.GroupBy != null)
        {
            if (!parameter.Columns.Any(id => ParameterSchema.SameId(id, parameter.GroupBy)))
                issues.Add(Error(SchemaIssueCode.UnknownReference, name, "groupBy", "\"" + parameter.GroupBy + "\" не является колонкой таблицы"));
            else if (Kind(schema.FindParameter(parameter.GroupBy)) != ParameterKind.Enum)
                issues.Add(Error(SchemaIssueCode.InvalidColumn, name, "groupBy", "группировать можно только по колонке Enum"));
        }
        if (parameter.Key != null && !parameter.Columns.Any(id => ParameterSchema.SameId(id, parameter.Key)))
            issues.Add(Error(SchemaIssueCode.UnknownReference, name, "key", "\"" + parameter.Key + "\" не является колонкой таблицы"));
    }

    private static void CheckEditor(ParameterEditor editor, ParameterKind kind, string name, List<SchemaIssue> issues)
    {
        var numeric = kind == ParameterKind.Double || kind == ParameterKind.Integer;
        NotApplicable(editor.Precision != null && kind != ParameterKind.Double, name, "editor.precision", "Double", issues);
        NotApplicable(editor.Min != null && !numeric, name, "editor.min", "Double/Integer", issues);
        NotApplicable(editor.Max != null && !numeric, name, "editor.max", "Double/Integer", issues);
        NotApplicable(editor.MaxLength != null && kind != ParameterKind.String, name, "editor.maxLength", "String", issues);
        NotApplicable(editor.Multiline != null && kind != ParameterKind.String, name, "editor.multiline", "String", issues);
        NotApplicable(editor.AllowCustom != null && kind != ParameterKind.Enum, name, "editor.allowCustom", "Enum", issues);
        if (editor.Precision < 0 || editor.Precision > 15) issues.Add(Error(SchemaIssueCode.InvalidEditor, name, "editor.precision", "допустимо от 0 до 15"));
        if (editor.MaxLength < 1 || editor.MaxLength > ParameterTypeRegistry.MaxStringBytes)
            issues.Add(Error(SchemaIssueCode.InvalidEditor, name, "editor.maxLength", "допустимо от 1 до " + ParameterTypeRegistry.MaxStringBytes.ToString(CultureInfo.InvariantCulture)));
        if (editor.Min > editor.Max) issues.Add(Error(SchemaIssueCode.InvalidEditor, name, "editor.min", "min больше max"));
        if (editor.VstColumn < 1 || editor.VstColumn > 4) issues.Add(Error(SchemaIssueCode.InvalidEditor, name, "editor.vstColumn", "допустимы колонки VST от 1 до 4"));
    }

    private static void CheckEnum(SchemaEnum item, string name, List<SchemaIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < item.Items.Count; i++)
        {
            var key = item.Items[i].Key;
            var itemName = name + " / элемент " + (string.IsNullOrEmpty(key) ? "#" + (i + 1).ToString(CultureInfo.InvariantCulture) : key);
            if (string.IsNullOrEmpty(key)) { issues.Add(Error(SchemaIssueCode.Required, itemName, "key", "не задан ключ")); continue; }
            if (key!.Trim() != key) issues.Add(Error(SchemaIssueCode.InvalidValue, itemName, "key", "ключ не может начинаться или заканчиваться пробелом"));
            if (ParameterTypeRegistry.Utf8Length(key) > ParameterTypeRegistry.MaxStringBytes)
                issues.Add(Error(SchemaIssueCode.InvalidValue, itemName, "key", "ключ длиннее " + ParameterTypeRegistry.MaxStringBytes.ToString(CultureInfo.InvariantCulture) + " байт UTF-8"));
            if (!seen.Add(key)) issues.Add(Error(SchemaIssueCode.DuplicateId, itemName, "key", "ключ повторяется в справочнике"));
        }
    }

    private void CheckTemplate(ParameterSchema schema, SchemaTemplate template, string name, List<SchemaIssue> issues)
    {
        if (template.EntityTypes != null && template.EntityTypes.Any(string.IsNullOrWhiteSpace))
            issues.Add(Error(SchemaIssueCode.InvalidValue, name, "entityTypes", "пустой тип примитива"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in template.Parameters)
        {
            if (string.IsNullOrEmpty(reference.Id)) { issues.Add(Error(SchemaIssueCode.Required, name, "parameters", "ссылка на параметр без id")); continue; }
            var parameter = schema.FindParameter(reference.Id);
            if (parameter is null) { issues.Add(Error(SchemaIssueCode.UnknownReference, name, "parameters", "параметр \"" + reference.Id + "\" не найден")); continue; }
            if (!seen.Add(reference.Id!)) issues.Add(Error(SchemaIssueCode.DuplicateReference, name, "parameters", "параметр \"" + reference.Id + "\" повторяется"));
            if (reference.Default != null && types.Find(parameter.Type)?.CheckValue(reference.Default, parameter, schema) is string problem)
                issues.Add(Error(SchemaIssueCode.InvalidDefault, name, "parameters", reference.Id + ".default: " + problem));
        }
    }

    /// <summary>Depth-first search over CalculatedString → CalculatedString references; each cycle is reported once.</summary>
    private void CheckCycles(ParameterSchema schema, List<SchemaIssue> issues)
    {
        var formulas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in schema.Parameters)
            if (parameter.Id != null && Kind(parameter) == ParameterKind.CalculatedString && parameter.Format != null && !formulas.ContainsKey(parameter.Id))
                formulas[parameter.Id] = CalculatedFormat.References(parameter.Format).ToList();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        void Visit(string id)
        {
            state[id] = 1;
            path.Add(id);
            foreach (var next in formulas[id].Where(formulas.ContainsKey))
            {
                var canonical = formulas.Keys.First(key => ParameterSchema.SameId(key, next));
                if (!state.TryGetValue(canonical, out var mark)) Visit(canonical);
                else if (mark == 1)
                {
                    var cycle = path.Skip(path.FindIndex(item => ParameterSchema.SameId(item, canonical))).Concat(new[] { canonical });
                    issues.Add(Error(SchemaIssueCode.CyclicFormat, "Параметр " + canonical, "format", "циклическая ссылка: " + string.Join(" → ", cycle)));
                }
            }
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
        foreach (var id in formulas.Keys.ToList())
            if (!state.ContainsKey(id)) Visit(id);
    }

    private ParameterKind? Kind(SchemaParameter? parameter) => types.Find(parameter?.Type)?.Kind;

    private static void NotApplicable(bool condition, string name, string field, string type, List<SchemaIssue> issues)
    {
        if (condition) issues.Add(Warning(SchemaIssueCode.NotApplicable, name, field, "поле применимо только к типу " + type + " и не используется"));
    }

    private static string Name(string kind, string? id, int index) =>
        kind + " " + (string.IsNullOrEmpty(id) ? "#" + (index + 1).ToString(CultureInfo.InvariantCulture) : id);

    private static SchemaIssue Error(SchemaIssueCode code, string name, string field, string message) => new SchemaIssue(code, name, field, message);
    private static SchemaIssue Warning(SchemaIssueCode code, string name, string field, string message) =>
        new SchemaIssue(code, name, field, message, SchemaIssueSeverity.Warning);
}
