using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// A field of one record of a <c>Repeat</c> (issue #61): <c>spec_2_Name</c> is the field <c>Name</c> of the record
/// «Спецификация №2» of the collection <c>spec</c>.
/// </summary>
public sealed class RepeatInstance
{
    internal RepeatInstance(SchemaParameter repeat, int number, RepeatField field, SchemaParameter? fieldParameter, string id)
    {
        Repeat = repeat; Number = number; Field = field; FieldParameter = fieldParameter; Id = id;
    }
    /// <summary>The <c>Repeat</c> parameter of the schema.</summary>
    public SchemaParameter Repeat { get; }
    /// <summary>Number of the record, from 1.</summary>
    public int Number { get; }
    public RepeatField Field { get; }
    /// <summary>The scalar parameter of the schema named by <see cref="RepeatField.Parameter"/>; null when it is missing.</summary>
    public SchemaParameter? FieldParameter { get; }
    /// <summary>The instance <c>Id</c> as it was parsed (the spelling of the object).</summary>
    public string Id { get; }
}

/// <summary>
/// Naming of the records of <c>Repeat</c> parameters (issue #61, option B). The object stores plain scalar pairs
/// <c>&lt;id&gt;_&lt;n&gt;_&lt;suffix&gt;</c>; the numbers start at 1 and have no leading zeros. The count is limited only by
/// the 16 KB XDATA limit of the object.
/// </summary>
public static class RepeatItems
{
    public const string TypeName = "Repeat";
    /// <summary>Placeholder of the record number in <see cref="SchemaParameter.Caption"/>: «Спецификация №{n}».</summary>
    public const string NumberPlaceholder = "{n}";
    /// <summary>Numbers longer than this are not record numbers (the 16 KB limit allows far fewer records).</summary>
    public const int MaxNumberDigits = 4;
    /// <summary>The validator checks that <c>&lt;id&gt;_999_&lt;suffix&gt;</c> still fits <see cref="SchemaValidator.MaxIdLength"/>.</summary>
    public const int CheckedNumber = 999;

    public static bool IsRepeat(SchemaParameter? parameter, ParameterTypeRegistry? types = null) =>
        parameter != null && (types ?? ParameterTypeRegistry.Default).Find(parameter.Type)?.Kind == ParameterKind.Repeat;

    /// <summary>A record field holds one value: String, Double, Integer, Boolean or Enum (not Table, Repeat or CalculatedString).</summary>
    public static bool IsFieldType(SchemaParameter? parameter, ParameterTypeRegistry? types = null)
    {
        var type = parameter is null ? null : (types ?? ParameterTypeRegistry.Default).Find(parameter.Type);
        return type != null && type.IsScalar && type.Kind != ParameterKind.CalculatedString && type.Kind != ParameterKind.Repeat;
    }

    /// <summary><c>spec_2_Name</c>: the prefix and suffix keep the spelling of the schema.</summary>
    public static string ItemId(string repeat, int number, string suffix) =>
        repeat + "_" + number.ToString(CultureInfo.InvariantCulture) + "_" + suffix;

    /// <summary>«Спецификация №2»: <c>{n}</c> of the caption is replaced, otherwise « №n» is appended.</summary>
    public static string Caption(SchemaParameter repeat, int number)
    {
        var caption = string.IsNullOrEmpty(repeat.Caption) ? repeat.Id ?? TypeName : repeat.Caption!;
        var text = number.ToString(CultureInfo.InvariantCulture);
        return caption.IndexOf(NumberPlaceholder, StringComparison.Ordinal) >= 0
            ? caption.Replace(NumberPlaceholder, text)
            : caption + " №" + text;
    }

    /// <summary>Caption of the collection without the number: «Спецификация №{n}» → «Спецификация».</summary>
    public static string CollectionCaption(SchemaParameter repeat)
    {
        if (!string.IsNullOrEmpty(repeat.Group)) return repeat.Group!;
        var caption = string.IsNullOrEmpty(repeat.Caption) ? repeat.Id ?? TypeName : repeat.Caption!;
        var index = caption.IndexOf(NumberPlaceholder, StringComparison.Ordinal);
        if (index < 0) return caption;
        var text = caption.Substring(0, index).TrimEnd(' ', '№', '#', '-', '_', ':').Trim();
        return text.Length == 0 ? caption.Replace(NumberPlaceholder, "").Trim() : text;
    }

    /// <summary>Parses <paramref name="id"/> as a field of a record of <paramref name="repeat"/> (case-insensitive).</summary>
    public static bool TryParse(SchemaParameter repeat, string? id, out int number, out RepeatField? field)
    {
        number = 0; field = null;
        if (id is null || string.IsNullOrEmpty(repeat.Id) || repeat.Fields is null) return false;
        var prefix = repeat.Id!.Length;
        if (id.Length < prefix + 4 || id[prefix] != '_' || !id.StartsWith(repeat.Id, StringComparison.OrdinalIgnoreCase)) return false;
        var start = prefix + 1;
        var end = start;
        while (end < id.Length && id[end] >= '0' && id[end] <= '9') end++;
        var digits = end - start;
        if (digits == 0 || digits > MaxNumberDigits || id[start] == '0' || end + 1 >= id.Length || id[end] != '_') return false;
        var suffix = id.Substring(end + 1);
        field = repeat.Fields.FirstOrDefault(item => string.Equals(item.Suffix, suffix, StringComparison.OrdinalIgnoreCase));
        if (field is null) return false;
        number = int.Parse(id.Substring(start, digits), NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The record number of <paramref name="id"/> in <paramref name="repeat"/> regardless of the suffix; 0 when it is not a record field.</summary>
    public static int NumberOf(SchemaParameter repeat, string? id) => TryParse(repeat, id, out var number, out _) ? number : 0;

    /// <summary>Record numbers stored in <paramref name="values"/>, ascending, without repetitions.</summary>
    public static IReadOnlyList<int> Numbers(ObjectValues values, SchemaParameter repeat) =>
        values.Values.Select(value => NumberOf(repeat, value.Id)).Where(number => number > 0).Distinct().OrderBy(number => number).ToArray();

    /// <summary>
    /// Formulas whose value depends on the records <paramref name="from"/>..<paramref name="to"/> of <paramref name="repeat"/>
    /// (directly or through other formulas), in the evaluation order.
    /// </summary>
    public static IReadOnlyList<Formula> Formulas(SchemaSnapshot schema, SchemaParameter repeat, int from = 1, int to = int.MaxValue)
    {
        var found = new HashSet<Formula>();
        foreach (var formula in schema.Formulas.Formulas)
            foreach (var reference in formula.References)
                if (NumberOf(repeat, reference) is int number && number >= from && number <= to)
                    foreach (var dependent in schema.Formulas.Dependents(reference)) found.Add(dependent);
        return found.OrderBy(formula => formula.Order).ToArray();
    }

    /// <summary>
    /// The report of the owner's decision for issue #61: formulas that refer to records whose numbers change after
    /// deleting (<c>from</c> = the deleted number, up to the last one) or moving (the two swapped numbers). Formulas refer
    /// to numbers, not to records, so their results now use other records; null when no formula is affected.
    /// </summary>
    public static string? ShiftReport(SchemaSnapshot schema, SchemaParameter repeat, int from, int to = int.MaxValue)
    {
        var lines = new List<string>();
        foreach (var formula in schema.Formulas.Formulas)
        {
            var references = formula.References.Where(reference => NumberOf(repeat, reference) is int number && number >= from && number <= to).ToArray();
            if (references.Length != 0) lines.Add(formula.Id + " (" + string.Join(", ", references) + ")");
        }
        return lines.Count == 0 ? null
            : "Номера записей «" + CollectionCaption(repeat) + "» изменились; формулы ссылаются на записи по номеру и теперь используют другие записи: " +
              string.Join("; ", lines) + ".";
    }

    /// <summary>Synthesized parameter of the field of one record: the type, enum and editor of the field parameter.</summary>
    public static SchemaParameter Instance(SchemaParameter repeat, int number, RepeatField field, SchemaParameter fieldParameter)
    {
        var result = new SchemaParameter
        {
            Id = ItemId(repeat.Id!, number, field.Suffix),
            Type = fieldParameter.Type,
            Caption = string.IsNullOrEmpty(fieldParameter.Caption) ? field.Suffix : fieldParameter.Caption,
            Description = fieldParameter.Description,
            Group = Caption(repeat, number),
            // No default: a formula over a missing record gets an empty value; a new record is filled by DefaultValue.
            Enum = fieldParameter.Enum,
            Editor = fieldParameter.Editor,
        };
        return result;
    }

    /// <summary>
    /// Default value of a field of a new record (the owner's decision for issue #61): the default of the field parameter,
    /// otherwise an empty string, 0 (or <c>editor.min</c> when 0 is out of range), false, or the first key of the enum.
    /// </summary>
    public static object? DefaultValue(SchemaParameter fieldParameter, Func<string?, SchemaEnum?> findEnum, ParameterTypeRegistry? types = null)
    {
        var type = (types ?? ParameterTypeRegistry.Default).Find(fieldParameter.Type);
        if (type is null) return null;
        if (fieldParameter.Default != null && !(fieldParameter.Default is JsonNull))
        {
            object raw = fieldParameter.Default switch
            {
                JsonString text => text.Value,
                JsonNumber number => number.ToDouble(),
                JsonBoolean flag => flag.Value,
                _ => fieldParameter.Default.ToJson(),
            };
            if (type.Convert(raw, out var converted) is null) return converted;
        }
        switch (type.Kind)
        {
            case ParameterKind.String: return "";
            case ParameterKind.Boolean: return false;
            case ParameterKind.Integer:
            case ParameterKind.Double:
            {
                var value = 0.0;
                var editor = fieldParameter.Editor;
                if (editor?.Min > value) value = editor.Min!.Value;
                if (editor?.Max < value) value = editor.Max!.Value;
                return type.Convert(type.Kind == ParameterKind.Integer ? (object)(int)Math.Ceiling(value) : value, out var converted) is null ? converted : null;
            }
            case ParameterKind.Enum:
                return findEnum(fieldParameter.Enum)?.Items.Select(item => item.Key).FirstOrDefault(key => !string.IsNullOrEmpty(key)) ?? "";
            default: return null;
        }
    }
}

/// <summary>
/// Parameters of a <see cref="SchemaSnapshot"/> by Id. Besides the declared parameters, the lookups resolve the fields of
/// <c>Repeat</c> records (<c>spec_2_Name</c>) to synthesized parameters typed by the field parameter, so the typing of
/// XDATA, formulas, the VST and the writer treat them as ordinary scalars. Enumeration returns only declared parameters.
/// </summary>
public sealed class SchemaParameterMap : IReadOnlyDictionary<string, SchemaParameter>
{
    private readonly Dictionary<string, SchemaParameter> declared;
    private readonly List<SchemaParameter> repeats;
    private readonly Dictionary<string, SchemaParameter?> instances = new Dictionary<string, SchemaParameter?>(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new object();

    internal SchemaParameterMap(Dictionary<string, SchemaParameter> declared)
    {
        this.declared = declared;
        repeats = declared.Values.Where(parameter => RepeatItems.IsRepeat(parameter) && parameter.Fields != null).ToList();
    }

    /// <summary>Declared <c>Repeat</c> parameters in the order of the schema.</summary>
    public IReadOnlyList<SchemaParameter> Repeats => repeats;

    public SchemaParameter this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys => declared.Keys;
    public IEnumerable<SchemaParameter> Values => declared.Values;
    public int Count => declared.Count;
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public IEnumerator<KeyValuePair<string, SchemaParameter>> GetEnumerator() => declared.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>True for a parameter declared in the schema (not a synthesized record field).</summary>
    public bool IsDeclared(string? id) => id != null && declared.ContainsKey(id);

    public bool TryGetValue(string key, out SchemaParameter value)
    {
        if (key != null && declared.TryGetValue(key, out value!)) return true;
        value = null!;
        if (key is null || repeats.Count == 0) return false;
        lock (sync)
        {
            if (!instances.TryGetValue(key, out var cached))
            {
                var found = FindRepeatInstance(key);
                cached = found?.FieldParameter is SchemaParameter field && RepeatItems.IsFieldType(field)
                    ? RepeatItems.Instance(found.Repeat, found.Number, found.Field, field)
                    : null;
                instances[key] = cached;
            }
            if (cached is null) return false;
            value = cached;
            return true;
        }
    }

    /// <summary>The record field named by <paramref name="id"/>, or null; a declared parameter of the same Id wins.</summary>
    public RepeatInstance? FindRepeatInstance(string? id)
    {
        if (id is null || declared.ContainsKey(id)) return null;
        foreach (var repeat in repeats)
            if (RepeatItems.TryParse(repeat, id, out var number, out var field))
                return new RepeatInstance(repeat, number, field!, declared.TryGetValue(field!.Parameter, out var parameter) ? parameter : null, id);
        return null;
    }

    /// <summary>The declared <c>Repeat</c> by Id (case-insensitive).</summary>
    public SchemaParameter? FindRepeat(string? id) => repeats.FirstOrDefault(repeat => ParameterSchema.SameId(repeat.Id, id));
}
