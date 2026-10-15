using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

public enum ParameterKind { String, Double, Integer, Boolean, Enum, CalculatedString, Table }

/// <summary>
/// A parameter type (TZXDATA 11): schema checks (stage 1) and the XDATA v2 value (stage 3).
/// Formatting and the VST editor factory are added to this interface by the later stages.
/// </summary>
public interface IParameterType
{
    string Name { get; }
    ParameterKind Kind { get; }
    /// <summary>Can be a <c>Table</c> column.</summary>
    bool IsScalar { get; }
    /// <summary>Returns the reason why <paramref name="value"/> is not a valid value of the parameter, or null.</summary>
    string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema);
    /// <summary>Group code of a stored scalar value (TZXDATA 6.3); 0 for <c>Table</c>.</summary>
    int XDataCode { get; }
    /// <summary>Reads a stored scalar value; false when its code or value does not fit the type (TZXDATA 6.5).</summary>
    bool TryRead(DataValue stored, out object value);
    /// <summary>
    /// Converts an edited value (typed, or text in the invariant culture) to the type of the parameter.
    /// Returns the reason why it cannot be converted, or null.
    /// </summary>
    string? Convert(object value, out object converted);
    /// <summary>The stored form of a value returned by <see cref="TryRead"/> or <see cref="Convert"/>.</summary>
    DataValue ToXData(object value);
}

/// <summary>Registry of parameter types; names are case-insensitive. Unknown types are reported, not dropped.</summary>
public sealed class ParameterTypeRegistry
{
    /// <summary>Maximum UTF-8 bytes of a 1000 XDATA string (TZXDATA 6.3).</summary>
    public const int MaxStringBytes = 255;
    private readonly Dictionary<string, IParameterType> types = new Dictionary<string, IParameterType>(StringComparer.OrdinalIgnoreCase);

    public static ParameterTypeRegistry Default { get; } = CreateBuiltIn();

    public IReadOnlyCollection<IParameterType> Types => types.Values;

    public static ParameterTypeRegistry CreateBuiltIn()
    {
        var registry = new ParameterTypeRegistry();
        registry.Register(new StringType());
        registry.Register(new NumberType(ParameterKind.Double));
        registry.Register(new NumberType(ParameterKind.Integer));
        registry.Register(new BooleanType());
        registry.Register(new EnumType());
        registry.Register(new CalculatedStringType());
        registry.Register(new TableType());
        return registry;
    }

    public void Register(IParameterType type)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));
        types[type.Name] = type;
    }

    public IParameterType? Find(string? name) => name != null && types.TryGetValue(name, out var type) ? type : null;

    internal static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    private sealed class StringType : IParameterType
    {
        public string Name => "String";
        public ParameterKind Kind => ParameterKind.String;
        public bool IsScalar => true;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema)
        {
            if (!(value is JsonString text)) return "ожидается строка";
            if (Utf8Length(text.Value) > MaxStringBytes) return "строка длиннее " + MaxStringBytes + " байт UTF-8";
            var maxLength = parameter.Editor?.MaxLength;
            return maxLength.HasValue && text.Value.Length > maxLength.Value ? "строка длиннее maxLength = " + maxLength.Value : null;
        }
        public int XDataCode => 1000;
        public bool TryRead(DataValue stored, out object value) => ReadText(stored, out value);
        public string? Convert(object value, out object converted) => ConvertText(value, out converted);
        public DataValue ToXData(object value) => new DataValue(1000, (string)value);
    }

    private sealed class NumberType : IParameterType
    {
        public NumberType(ParameterKind kind) { Kind = kind; }
        public string Name => Kind.ToString();
        public ParameterKind Kind { get; }
        public bool IsScalar => true;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema)
        {
            if (!(value is JsonNumber number)) return Kind == ParameterKind.Integer ? "ожидается целое число" : "ожидается число";
            // Integer is stored as 1071, a 32-bit value (TZXDATA 6.3).
            if (Kind == ParameterKind.Integer && !number.TryGetInt32(out _)) return "ожидается 32-битное целое число";
            var result = number.ToDouble();
            if (double.IsInfinity(result)) return "число вне диапазона Double";
            var editor = parameter.Editor;
            if (editor?.Min is double min && result < min) return "значение меньше min";
            if (editor?.Max is double max && result > max) return "значение больше max";
            return null;
        }
        public int XDataCode => Kind == ParameterKind.Integer ? 1071 : 1040;
        public bool TryRead(DataValue stored, out object value)
        {
            value = stored.Value;
            if (stored.TypeCode != XDataCode) return false;
            if (Kind == ParameterKind.Integer) return stored.Value is int;
            return stored.Value is double number && !double.IsNaN(number) && !double.IsInfinity(number);
        }
        public string? Convert(object value, out object converted)
        {
            converted = value;
            switch (value)
            {
                case int number when Kind == ParameterKind.Integer: converted = number; return null;
                case short number: return Convert((int)number, out converted);
                case long number:
                    if (Kind == ParameterKind.Integer && (number < int.MinValue || number > int.MaxValue)) return "ожидается 32-битное целое число";
                    converted = Kind == ParameterKind.Integer ? (object)(int)number : number; return null;
                case int number: converted = (double)number; return null;
                case double number:
                    if (double.IsNaN(number) || double.IsInfinity(number)) return "ожидается конечное число";
                    if (Kind == ParameterKind.Double) { converted = number; return null; }
                    if (Math.Floor(number) != number || number < int.MinValue || number > int.MaxValue) return "ожидается 32-битное целое число";
                    converted = (int)number; return null;
                case string text:
                    // A comma is accepted as the decimal separator of the Russian culture.
                    var trimmed = text.Trim();
                    if (Kind == ParameterKind.Integer)
                        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                            ? Convert(integer, out converted) : "ожидается 32-битное целое число";
                    return double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                        ? Convert(real, out converted) : "ожидается число";
                default: return Kind == ParameterKind.Integer ? "ожидается целое число" : "ожидается число";
            }
        }
        public DataValue ToXData(object value) => Kind == ParameterKind.Integer ? new DataValue(1071, (int)value) : new DataValue(1040, (double)value);
    }

    private sealed class BooleanType : IParameterType
    {
        public string Name => "Boolean";
        public ParameterKind Kind => ParameterKind.Boolean;
        public bool IsScalar => true;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema) =>
            value is JsonBoolean ? null : "ожидается true или false";
        public int XDataCode => 1070;
        public bool TryRead(DataValue stored, out object value)
        {
            // 1070 is a 16-bit integer; only 0 and 1 are a Boolean (TZXDATA 6.3).
            value = stored.Value;
            if (stored.TypeCode != 1070 || !(stored.Value is short flag) || (flag != 0 && flag != 1)) return false;
            value = flag == 1;
            return true;
        }
        public string? Convert(object value, out object converted)
        {
            converted = value;
            switch (value)
            {
                case bool flag: converted = flag; return null;
                case short number when number == 0 || number == 1: converted = number == 1; return null;
                case int number when number == 0 || number == 1: converted = number == 1; return null;
                case string text:
                    var trimmed = text.Trim();
                    if (trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed == "Да") { converted = true; return null; }
                    if (trimmed == "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed == "Нет") { converted = false; return null; }
                    return "ожидается true или false";
                default: return "ожидается true или false";
            }
        }
        public DataValue ToXData(object value) => new DataValue(1070, (short)((bool)value ? 1 : 0));
    }

    private sealed class EnumType : IParameterType
    {
        public string Name => "Enum";
        public ParameterKind Kind => ParameterKind.Enum;
        public bool IsScalar => true;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema)
        {
            // XDATA stores the key, not the caption (TZXDATA 6.1, П5).
            if (!(value is JsonString key)) return "ожидается ключ справочника (строка)";
            if (Utf8Length(key.Value) > MaxStringBytes) return "ключ длиннее " + MaxStringBytes + " байт UTF-8";
            var reference = schema.FindEnum(parameter.Enum);
            if (reference is null || parameter.Editor?.AllowCustom == true) return null;
            return reference.FindItem(key.Value) is null ? "ключ \"" + key.Value + "\" отсутствует в справочнике " + reference.Id : null;
        }
        public int XDataCode => 1000;
        public bool TryRead(DataValue stored, out object value) => ReadText(stored, out value);
        public string? Convert(object value, out object converted) => ConvertText(value, out converted);
        public DataValue ToXData(object value) => new DataValue(1000, (string)value);
    }

    private sealed class CalculatedStringType : IParameterType
    {
        public string Name => "CalculatedString";
        public ParameterKind Kind => ParameterKind.CalculatedString;
        public bool IsScalar => true;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema)
        {
            if (!(value is JsonString text)) return "ожидается строка";
            return Utf8Length(text.Value) > MaxStringBytes ? "строка длиннее " + MaxStringBytes + " байт UTF-8" : null;
        }
        public int XDataCode => 1000;
        public bool TryRead(DataValue stored, out object value) => ReadText(stored, out value);
        public string? Convert(object value, out object converted) => ConvertText(value, out converted);
        public DataValue ToXData(object value) => new DataValue(1000, (string)value);
    }

    private sealed class TableType : IParameterType
    {
        public string Name => "Table";
        public ParameterKind Kind => ParameterKind.Table;
        public bool IsScalar => false;
        public string? CheckValue(JsonValue value, SchemaParameter parameter, ParameterSchema schema) =>
            "значение по умолчанию для Table не поддерживается";
        public int XDataCode => 0;
        public bool TryRead(DataValue stored, out object value) { value = stored.Value; return false; }
        public string? Convert(object value, out object converted) { converted = value; return "значение Table задаётся строками"; }
        public DataValue ToXData(object value) => throw new InvalidOperationException("Table хранится списком строк.");
    }

    private static bool ReadText(DataValue stored, out object value)
    {
        value = stored.Value;
        return stored.TypeCode == 1000 && stored.Value is string;
    }

    private static string? ConvertText(object value, out object converted)
    {
        converted = value is IFormattable formatted ? formatted.ToString(null, CultureInfo.InvariantCulture) : value as string ?? "";
        return value is string || value is IFormattable ? null : "ожидается строка";
    }
}

/// <summary>Fragment of a parsed <c>CalculatedString</c> format: literal text or an <c>@@[Id]</c> reference.</summary>
public sealed class FormatFragment
{
    private FormatFragment(string? text, string? reference, bool useKey) { Text = text; Reference = reference; UseKey = useKey; }
    public string? Text { get; }
    public string? Reference { get; }
    /// <summary><c>@@[Id:key]</c>: substitute the enum key instead of its caption (TZXDATA 7.2).</summary>
    public bool UseKey { get; }
    public static FormatFragment Literal(string text) => new FormatFragment(text, null, false);
    public static FormatFragment Parameter(string id, bool useKey) => new FormatFragment(null, id, useKey);
}

/// <summary>Parser of the <c>CalculatedString</c> syntax (TZXDATA 7.1): <c>@@[Id]</c>, <c>@@[Id:key]</c>, <c>@@@@</c>.</summary>
public static class CalculatedFormat
{
    /// <summary>Returns fragments; <paramref name="error"/> is set for an unclosed or malformed reference.</summary>
    public static IReadOnlyList<FormatFragment> Parse(string format, out string? error)
    {
        error = null;
        var fragments = new List<FormatFragment>();
        var literal = new StringBuilder();
        var index = 0;
        while (index < format.Length)
        {
            if (string.CompareOrdinal(format, index, "@@@@", 0, 4) == 0) { literal.Append("@@"); index += 4; continue; }
            if (string.CompareOrdinal(format, index, "@@[", 0, 3) != 0) { literal.Append(format[index++]); continue; }
            var end = format.IndexOf(']', index + 3);
            if (end < 0) { error ??= "незакрытая ссылка @@[ в позиции " + index; literal.Append(format, index, format.Length - index); break; }
            var body = format.Substring(index + 3, end - index - 3);
            var colon = body.IndexOf(':');
            var id = colon < 0 ? body : body.Substring(0, colon);
            var modifier = colon < 0 ? null : body.Substring(colon + 1);
            if (!SchemaValidator.IsValidId(id) || (modifier != null && modifier != "key"))
            {
                error ??= "недопустимая ссылка @@[" + body + "]";
                literal.Append(format, index, end + 1 - index);
            }
            else
            {
                if (literal.Length != 0) { fragments.Add(FormatFragment.Literal(literal.ToString())); literal.Clear(); }
                fragments.Add(FormatFragment.Parameter(id, modifier != null));
            }
            index = end + 1;
        }
        if (literal.Length != 0) fragments.Add(FormatFragment.Literal(literal.ToString()));
        return fragments;
    }

    public static IEnumerable<string> References(string format) =>
        Parse(format, out _).Where(fragment => fragment.Reference != null).Select(fragment => fragment.Reference!);
}
