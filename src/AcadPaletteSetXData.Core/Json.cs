using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Minimal ordered JSON DOM: net48 has no System.Text.Json and the release must stay one DLL.
/// Numbers keep their source text, objects keep property order, so unknown data round-trips unchanged.
/// </summary>
public abstract class JsonValue
{
    public static JsonValue Parse(string text) => new JsonReader(text).ReadDocument();

    public string ToJson() { var builder = new StringBuilder(); JsonWriter.Write(this, builder, 0); return builder.ToString(); }

    /// <summary>
    /// One line without insignificant whitespace. Besides control characters it escapes <c>\</c> and <c>^</c>
    /// as <c>\u005c</c>/<c>\u005e</c>, so DXF never sees <c>\U+XXXX</c> or caret sequences and the text survives DWG/DXF.
    /// </summary>
    public string ToCompactJson() { var builder = new StringBuilder(); JsonWriter.WriteCompact(this, builder); return builder.ToString(); }

    public override string ToString() => ToJson();

    /// <summary>Structural equality; numbers compare by value, so <c>2.5</c> equals <c>2.50</c>.</summary>
    public static bool DeepEquals(JsonValue? left, JsonValue? right)
    {
        if (left is null || right is null) return left is null && right is null;
        switch (left)
        {
            case JsonString text: return right is JsonString other && text.Value == other.Value;
            case JsonBoolean flag: return right is JsonBoolean otherFlag && flag.Value == otherFlag.Value;
            case JsonNull _: return right is JsonNull;
            case JsonNumber number:
                return right is JsonNumber otherNumber && (number.Text == otherNumber.Text ||
                    (number.TryGetDecimal(out var a) && otherNumber.TryGetDecimal(out var b) ? a == b : number.ToDouble() == otherNumber.ToDouble()));
            case JsonArray array:
                return right is JsonArray otherArray && array.Count == otherArray.Count &&
                    array.Zip(otherArray, (x, y) => DeepEquals(x, y)).All(equal => equal);
            case JsonObject obj:
                return right is JsonObject otherObject && obj.Count == otherObject.Count &&
                    obj.All(property => otherObject.TryGetValue(property.Key, out var value) && DeepEquals(property.Value, value));
            default: return false;
        }
    }
}

public sealed class JsonString : JsonValue
{
    public JsonString(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
    public string Value { get; }
}

public sealed class JsonBoolean : JsonValue
{
    public static readonly JsonBoolean True = new JsonBoolean(true);
    public static readonly JsonBoolean False = new JsonBoolean(false);
    private JsonBoolean(bool value) { Value = value; }
    public bool Value { get; }
    public static JsonBoolean From(bool value) => value ? True : False;
}

public sealed class JsonNull : JsonValue
{
    public static readonly JsonNull Instance = new JsonNull();
    private JsonNull() { }
}

public sealed class JsonNumber : JsonValue
{
    /// <summary>Keeps the exact token (<c>2.50</c>, <c>1e3</c>); text must be a valid JSON number.</summary>
    public JsonNumber(string text)
    {
        if (!JsonReader.IsNumber(text)) throw new FormatException("Недопустимое число JSON: " + text);
        Text = text;
    }
    public JsonNumber(double value) : this(Format(value)) { }
    public JsonNumber(long value) : this(value.ToString(CultureInfo.InvariantCulture)) { }
    public string Text { get; }

    public bool IsInteger => Text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 || (TryGetDecimal(out var value) && decimal.Truncate(value) == value);

    public double ToDouble() => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);

    public bool TryGetInt32(out int value)
    {
        value = 0;
        if (!TryGetDecimal(out var number) || decimal.Truncate(number) != number || number < int.MinValue || number > int.MaxValue) return false;
        value = (int)number;
        return true;
    }

    public bool TryGetDecimal(out decimal value) =>
        decimal.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "JSON не поддерживает NaN и бесконечность.");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

public sealed class JsonArray : JsonValue, IReadOnlyList<JsonValue>
{
    private readonly List<JsonValue> items = new List<JsonValue>();
    public JsonArray() { }
    public JsonArray(IEnumerable<JsonValue> values) { items.AddRange(values); }
    public int Count => items.Count;
    public JsonValue this[int index] => items[index];
    public void Add(JsonValue value) => items.Add(value ?? throw new ArgumentNullException(nameof(value)));
    public IEnumerator<JsonValue> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => items.GetEnumerator();
}

/// <summary>Ordered object; property names are unique and case-sensitive as in JSON.</summary>
public sealed class JsonObject : JsonValue, IReadOnlyCollection<KeyValuePair<string, JsonValue>>
{
    private readonly List<KeyValuePair<string, JsonValue>> properties = new List<KeyValuePair<string, JsonValue>>();
    public int Count => properties.Count;

    public JsonValue? this[string name] => TryGetValue(name, out var value) ? value : null;

    public bool TryGetValue(string name, out JsonValue value)
    {
        foreach (var property in properties)
            if (property.Key == name) { value = property.Value; return true; }
        value = JsonNull.Instance;
        return false;
    }

    public bool ContainsKey(string name) => properties.Any(property => property.Key == name);

    /// <summary>Appends a property; a repeated name is rejected because JSON readers disagree about it.</summary>
    public void Add(string name, JsonValue value)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (ContainsKey(name)) throw new ArgumentException("Повторяющееся поле JSON: " + name, nameof(name));
        properties.Add(new KeyValuePair<string, JsonValue>(name, value ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>Replaces the value in place, keeping the property position, or appends a new property.</summary>
    public void Set(string name, JsonValue value)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));
        var index = properties.FindIndex(property => property.Key == name);
        if (index < 0) properties.Add(new KeyValuePair<string, JsonValue>(name, value));
        else properties[index] = new KeyValuePair<string, JsonValue>(name, value);
    }

    public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator() => properties.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => properties.GetEnumerator();
}

public sealed class JsonException : FormatException
{
    public JsonException(string message, int line, int column)
        : base(message + " (строка " + line.ToString(CultureInfo.InvariantCulture) + ", столбец " + column.ToString(CultureInfo.InvariantCulture) + ")")
    { Line = line; Column = column; }
    public int Line { get; }
    public int Column { get; }
}

/// <summary>Strict RFC 8259 reader with depth/size limits; the leading UTF-8 BOM is ignored.</summary>
internal sealed class JsonReader
{
    public const int MaxDepth = 64;
    public const int MaxCharacters = 16 * 1024 * 1024;
    private readonly string text;
    private int position;
    private int depth;

    public JsonReader(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        this.text = text.Length != 0 && text[0] == '﻿' ? text.Substring(1) : text;
        if (this.text.Length > MaxCharacters) throw Error("Превышен размер JSON", 0);
    }

    public JsonValue ReadDocument()
    {
        SkipWhitespace();
        var value = ReadValue();
        SkipWhitespace();
        if (position != text.Length) throw Error("Лишние символы после JSON", position);
        return value;
    }

    internal static bool IsNumber(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var reader = new JsonReader(candidate!);
        try { return reader.ReadNumberEnd() == candidate!.Length; }
        catch (JsonException) { return false; }
    }

    private JsonValue ReadValue()
    {
        if (position >= text.Length) throw Error("Неожиданный конец JSON", position);
        switch (text[position])
        {
            case '{': return ReadObject();
            case '[': return ReadArray();
            case '"': return new JsonString(ReadString());
            case 't': ReadLiteral("true"); return JsonBoolean.True;
            case 'f': ReadLiteral("false"); return JsonBoolean.False;
            case 'n': ReadLiteral("null"); return JsonNull.Instance;
            default:
                var start = position;
                position = ReadNumberEnd();
                return new JsonNumber(text.Substring(start, position - start));
        }
    }

    private JsonObject ReadObject()
    {
        Enter();
        var result = new JsonObject();
        position++;
        SkipWhitespace();
        if (Peek() == '}') { position++; depth--; return result; }
        while (true)
        {
            SkipWhitespace();
            if (Peek() != '"') throw Error("Ожидается имя поля", position);
            var nameStart = position;
            var name = ReadString();
            if (result.ContainsKey(name)) throw Error("Повторяющееся поле \"" + name + "\"", nameStart);
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();
            result.Add(name, ReadValue());
            SkipWhitespace();
            if (Peek() == ',') { position++; continue; }
            Expect('}');
            depth--;
            return result;
        }
    }

    private JsonArray ReadArray()
    {
        Enter();
        var result = new JsonArray();
        position++;
        SkipWhitespace();
        if (Peek() == ']') { position++; depth--; return result; }
        while (true)
        {
            SkipWhitespace();
            result.Add(ReadValue());
            SkipWhitespace();
            if (Peek() == ',') { position++; continue; }
            Expect(']');
            depth--;
            return result;
        }
    }

    private string ReadString()
    {
        position++;
        var builder = new StringBuilder();
        while (true)
        {
            if (position >= text.Length) throw Error("Незакрытая строка", position);
            var c = text[position++];
            if (c == '"') return builder.ToString();
            if (c < ' ') throw Error("Управляющий символ в строке", position - 1);
            if (c != '\\') { builder.Append(c); continue; }
            if (position >= text.Length) throw Error("Незакрытая строка", position);
            var escape = text[position++];
            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (position + 4 > text.Length || !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        throw Error("Недопустимая последовательность \\u", position - 2);
                    builder.Append((char)code);
                    position += 4;
                    break;
                default: throw Error("Недопустимая escape-последовательность", position - 2);
            }
        }
    }

    private int ReadNumberEnd()
    {
        var start = position;
        var index = position;
        if (index < text.Length && text[index] == '-') index++;
        if (index < text.Length && text[index] == '0') index++;
        else if (index < text.Length && text[index] >= '1' && text[index] <= '9') index = Digits(index);
        else throw Error("Ожидается значение JSON", start);
        if (index < text.Length && text[index] == '.')
        {
            var fraction = Digits(index + 1);
            if (fraction == index + 1) throw Error("Недопустимое число", start);
            index = fraction;
        }
        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            index++;
            if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
            var exponent = Digits(index);
            if (exponent == index) throw Error("Недопустимое число", start);
            index = exponent;
        }
        return index;
    }

    private int Digits(int index)
    {
        while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
        return index;
    }

    private void ReadLiteral(string literal)
    {
        if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) throw Error("Ожидается значение JSON", position);
        position += literal.Length;
    }

    private void Enter()
    {
        if (++depth > MaxDepth) throw Error("Превышена глубина JSON", position);
    }

    private char Peek() => position < text.Length ? text[position] : '\0';

    private void Expect(char expected)
    {
        if (Peek() != expected) throw Error(position >= text.Length ? "Неожиданный конец JSON" : "Ожидается '" + expected + "'", position);
        position++;
    }

    private void SkipWhitespace()
    {
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\n' || text[position] == '\r')) position++;
    }

    private JsonException Error(string message, int offset)
    {
        int line = 1, column = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return new JsonException(message, line, column);
    }
}

/// <summary>
/// Deterministic indented writer: two spaces, LF line breaks. An object or array of scalars is written
/// on one line when it fits <see cref="MaxInlineWidth"/>, otherwise one item per line.
/// Non-ASCII text is written as is (UTF-8 file), numbers keep their source token.
/// </summary>
internal static class JsonWriter
{
    public const int MaxInlineWidth = 100;

    public static void Write(JsonValue value, StringBuilder builder, int indent)
    {
        switch (value)
        {
            case JsonString text: WriteString(text.Value, builder); break;
            case JsonNumber number: builder.Append(number.Text); break;
            case JsonBoolean flag: builder.Append(flag.Value ? "true" : "false"); break;
            case JsonNull _: builder.Append("null"); break;
            case JsonArray array:
                if (array.Count == 0) { builder.Append("[]"); break; }
                if (TryInline(array, array, indent, builder)) break;
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i != 0) builder.Append(',');
                    NewLine(builder, indent + 1);
                    Write(array[i], builder, indent + 1);
                }
                NewLine(builder, indent);
                builder.Append(']');
                break;
            case JsonObject obj:
                if (obj.Count == 0) { builder.Append("{}"); break; }
                if (TryInline(obj, obj.Select(property => property.Value), indent, builder)) break;
                builder.Append('{');
                var first = true;
                foreach (var property in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    NewLine(builder, indent + 1);
                    WriteString(property.Key, builder);
                    builder.Append(": ");
                    Write(property.Value, builder, indent + 1);
                }
                NewLine(builder, indent);
                builder.Append('}');
                break;
            default: throw new ArgumentException("Неизвестный тип значения JSON.", nameof(value));
        }
    }

    public static void WriteCompact(JsonValue value, StringBuilder builder)
    {
        switch (value)
        {
            case JsonString text: WriteString(text.Value, builder, dxfSafe: true); break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i != 0) builder.Append(',');
                    WriteCompact(array[i], builder);
                }
                builder.Append(']');
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var property in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(property.Key, builder, dxfSafe: true);
                    builder.Append(':');
                    WriteCompact(property.Value, builder);
                }
                builder.Append('}');
                break;
            default: Write(value, builder, 0); break;
        }
    }

    private static bool TryInline(JsonValue container, IEnumerable<JsonValue> children, int indent, StringBuilder builder)
    {
        if (children.Any(child => child is JsonArray || child is JsonObject)) return false;
        var line = new StringBuilder();
        if (container is JsonObject obj)
        {
            line.Append("{ ");
            line.Append(string.Join(", ", obj.Select(property =>
            {
                var item = new StringBuilder();
                WriteString(property.Key, item);
                item.Append(": ");
                Write(property.Value, item, 0);
                return item.ToString();
            })));
            line.Append(" }");
        }
        else
        {
            line.Append("[ ");
            line.Append(string.Join(", ", children.Select(child => { var item = new StringBuilder(); Write(child, item, 0); return item.ToString(); })));
            line.Append(" ]");
        }
        if (indent * 2 + line.Length > MaxInlineWidth) return false;
        builder.Append(line);
        return true;
    }

    private static void NewLine(StringBuilder builder, int indent) => builder.Append('\n').Append(' ', indent * 2);

    private static void WriteString(string value, StringBuilder builder, bool dxfSafe = false)
    {
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append(dxfSafe ? "\\u005c" : "\\\\"); break;
                case '^' when dxfSafe: builder.Append("\\u005e"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) builder.Append(c).Append(value[++i]);
                    // Control characters and lone surrogates (not encodable in UTF-8) are escaped to stay lossless.
                    else if (c < ' ' || char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
