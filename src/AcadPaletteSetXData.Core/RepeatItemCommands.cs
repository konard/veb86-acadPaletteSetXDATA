using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// The program layer of the records of a <c>Repeat</c> (issue #61, docs/repeating-parameters.md, section 4): the
/// commands <c>VELEC_ITEM_ADD</c>/<c>VELEC_ITEM_REMOVE_LAST</c> and the LISP functions <c>velec-item-add</c>,
/// <c>velec-item-remove-last</c>, <c>velec-item-count</c> and <c>velec-item-get</c>. The AutoCAD project only converts
/// <c>ResultBuffer</c> to <see cref="DataValue"/> with the codes of <c>LispDataType</c> below and back.
/// </summary>
public static class RepeatItemCommands
{
    /// <summary>Codes of <c>Autodesk.AutoCAD.Runtime.LispDataType</c>.</summary>
    public const int LispDouble = 5001, LispInt16 = 5003, LispText = 5005, LispObjectId = 5006, LispInt32 = 5010,
        LispListBegin = 5016, LispListEnd = 5017, LispDottedPair = 5018, LispNil = 5019, LispT = 5021;

    /// <summary>The name of a record without its number: «Спецификация №{n}» → «Спецификация».</summary>
    public static string Keyword(SchemaParameter repeat)
    {
        if (repeat is null) throw new ArgumentNullException(nameof(repeat));
        var caption = repeat.Caption ?? "";
        var at = caption.IndexOf(RepeatItems.NumberPlaceholder, StringComparison.Ordinal);
        if (at >= 0) caption = caption.Substring(0, at).TrimEnd(' ', '№', '#', '-', '_');
        return caption.Trim().Length == 0 ? repeat.Id! : caption.Trim();
    }

    /// <summary>
    /// The collection of an answer of a command or an argument of a LISP function: its <c>Id</c> (<c>spec</c>), the
    /// name of a record (<c>Спецификация</c>) or the caption of the collection; case-insensitive, a leading <c>_</c> of a
    /// macro is ignored. Null when nothing or more than one collection matches.
    /// </summary>
    public static SchemaParameter? Find(SchemaSnapshot schema, string? text)
    {
        if (schema is null) throw new ArgumentNullException(nameof(schema));
        var name = (text ?? "").Trim().TrimStart('_').Trim();
        if (name.Length == 0) return null;
        var repeats = schema.Parameters.Repeats;
        var byId = schema.Parameters.FindRepeat(name);
        if (byId != null) return byId;
        foreach (var by in new Func<SchemaParameter, string>[] { Keyword, RepeatItems.CollectionCaption })
        {
            var found = repeats.Where(repeat => string.Equals(by(repeat), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 1) return found[0];
        }
        return null;
    }

    /// <summary>The list of collections for a prompt: «spec — Спецификация/conn — Соединение».</summary>
    public static string Options(SchemaSnapshot schema) =>
        string.Join("/", schema.Parameters.Repeats.Select(repeat => repeat.Id + " — " + Keyword(repeat)));

    /// <summary>Number of records of the object (velec-item-count).</summary>
    public static int Count(ObjectValues values, SchemaParameter repeat) => RepeatItems.Numbers(values, repeat).Count;

    /// <summary>The largest record number of the object; 0 — no records.</summary>
    public static int Last(ObjectValues values, SchemaParameter repeat) => RepeatItems.Numbers(values, repeat).LastOrDefault();

    /// <summary>
    /// The fields of record <paramref name="number"/> stored in the object, in the order of the schema, under the suffixes of
    /// the schema (velec-item-get); null when the object has no such record.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object>>? Get(ObjectValues values, SchemaParameter repeat, int number)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (repeat?.Fields is null || !RepeatItems.Numbers(values, repeat).Contains(number)) return null;
        var result = new List<KeyValuePair<string, object>>();
        foreach (var field in repeat.Fields)
            if (values.Find(RepeatItems.ItemId(repeat.Id!, number, field.Suffix))?.Value is object value)
                result.Add(new KeyValuePair<string, object>(field.Suffix, value));
        return result;
    }

    /// <summary>
    /// The field values of a new record from the list of a LISP function: <c>(("target" . "ЩО1.1") ("cable" . "ВВГ"))</c>
    /// or <c>(("target" "ЩО1.1"))</c>; <c>nil</c> — every field gets its default. An unknown suffix is an error.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object>> Cells(SchemaParameter repeat, IReadOnlyList<DataValue>? list)
    {
        if (repeat?.Fields is null) throw new ArgumentNullException(nameof(repeat));
        var result = new List<KeyValuePair<string, object>>();
        if (list is null || list.Count == 0 || list.Count == 1 && list[0].TypeCode == LispNil) return result;
        if (list[0].TypeCode != LispListBegin || list[list.Count - 1].TypeCode != LispListEnd)
            throw new ArgumentException("Значения полей — список пар: '((\"поле\" . значение) …).");
        var i = 1;
        while (i < list.Count - 1)
        {
            if (list[i].TypeCode != LispListBegin || i + 3 >= list.Count || list[i + 1].TypeCode != LispText)
                throw new ArgumentException("Значения полей — список пар: '((\"поле\" . значение) …).");
            var suffix = (string)list[i + 1].Value;
            var value = list[i + 2];
            var end = list[i + 3].TypeCode;
            if (end != LispDottedPair && end != LispListEnd)
                throw new ArgumentException("Поле «" + suffix + "»: ожидается одно значение.");
            var field = repeat.Fields.FirstOrDefault(item => string.Equals(item.Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("У записи " + repeat.Id + " нет поля «" + suffix + "» (поля: " +
                    string.Join(", ", repeat.Fields.Select(item => item.Suffix)) + ").");
            result.Add(new KeyValuePair<string, object>(field.Suffix, Atom(value) ?? throw new ArgumentException("Поле «" + suffix + "»: значение не задано.")));
            i += 4;
        }
        return result;
    }

    /// <summary>Splits the arguments of a LISP function: an atom is one element, a list — its elements with the brackets.</summary>
    public static IReadOnlyList<IReadOnlyList<DataValue>> Arguments(IReadOnlyList<DataValue>? arguments)
    {
        var result = new List<IReadOnlyList<DataValue>>();
        if (arguments is null) return result;
        for (var i = 0; i < arguments.Count;)
        {
            var start = i;
            var depth = 0;
            do
            {
                if (arguments[i].TypeCode == LispListBegin) depth++;
                else if (arguments[i].TypeCode == LispListEnd || arguments[i].TypeCode == LispDottedPair) depth--;
                i++;
            } while (depth > 0 && i < arguments.Count);
            result.Add(arguments.Skip(start).Take(i - start).ToArray());
        }
        return result;
    }

    /// <summary>A record number argument: an integer or a real without a fraction, from 1.</summary>
    public static int Number(IReadOnlyList<DataValue> argument)
    {
        if (argument.Count == 1)
            switch (argument[0].Value)
            {
                case short value when value > 0: return value;
                case int value when value > 0: return value;
                case double value when value >= 1 && value <= int.MaxValue && Math.Floor(value) == value: return (int)value;
            }
        throw new ArgumentException("Номер записи — целое число от 1.");
    }

    /// <summary>The value of an atom: text, number; T → true; nil → null.</summary>
    public static object? Atom(DataValue value) => value.TypeCode switch
    {
        LispText => value.Value as string ?? "",
        LispDouble => System.Convert.ToDouble(value.Value, CultureInfo.InvariantCulture),
        LispInt16 => (int)System.Convert.ToInt16(value.Value, CultureInfo.InvariantCulture),
        LispInt32 => System.Convert.ToInt32(value.Value, CultureInfo.InvariantCulture),
        LispT => true,
        LispNil => null,
        _ => throw new ArgumentException("Неподдерживаемое значение LISP: " + value.Value + "."),
    };

    /// <summary>
    /// The result of velec-item-get: <c>(("pos" . "2") ("Name" . "Лампа") ("col" . 2.0))</c>; a <c>Boolean</c> field
    /// is 1 or 0, an enum field — its key.
    /// </summary>
    public static IReadOnlyList<DataValue> ToLisp(IReadOnlyList<KeyValuePair<string, object>> record)
    {
        var result = new List<DataValue> { new DataValue(LispListBegin, "") };
        foreach (var pair in record)
        {
            result.Add(new DataValue(LispListBegin, ""));
            result.Add(new DataValue(LispText, pair.Key));
            result.Add(pair.Value switch
            {
                string text => new DataValue(LispText, text),
                double number => new DataValue(LispDouble, number),
                float number => new DataValue(LispDouble, (double)number),
                int number => new DataValue(LispInt32, number),
                short number => new DataValue(LispInt16, number),
                bool flag => new DataValue(LispInt16, (short)(flag ? 1 : 0)),
                var other => new DataValue(LispText, XDataV2.FormatValue(other)),
            });
            result.Add(new DataValue(LispDottedPair, ""));
        }
        result.Add(new DataValue(LispListEnd, ""));
        return result;
    }

    /// <summary>
    /// A line of the command: «Добавлено: Соединение №3 (2 объекта)»; different numbers of the objects are listed
    /// one by one: «Удалено: Соединение №3 (1 объект), Соединение №2 (1 объект)».
    /// </summary>
    public static string Summary(string verb, SchemaParameter repeat, IEnumerable<int> numbers) =>
        verb + ": " + string.Join(", ", numbers.Where(number => number > 0).GroupBy(number => number).OrderByDescending(group => group.Key)
            .Select(group => RepeatItems.Caption(repeat, group.Key) + " (" + ObjectsText(group.Count()) + ")"));

    /// <summary>Russian plural: «1 объект», «3 объекта», «10 объектов».</summary>
    public static string ObjectsText(int count)
    {
        var tens = count % 100;
        var ones = count % 10;
        var word = tens >= 11 && tens <= 14 ? "объектов" : ones == 1 ? "объект" : ones >= 2 && ones <= 4 ? "объекта" : "объектов";
        return count.ToString(CultureInfo.InvariantCulture) + " " + word;
    }

    /// <summary>The prompt of a field of a new record: «Наименование (Name) &lt;пусто&gt;», «Кол. (col) &lt;1&gt;».</summary>
    public static string FieldPrompt(SchemaSnapshot schema, SchemaParameter repeat, RepeatField field)
    {
        var parameter = schema.Parameters.TryGetValue(field.Parameter, out var found) ? found : null;
        var caption = string.IsNullOrEmpty(parameter?.Caption) ? field.Suffix : parameter!.Caption + " (" + field.Suffix + ")";
        var value = parameter is null ? null
            : RepeatItems.DefaultValue(parameter, id => id != null && schema.Enums.TryGetValue(id, out var item) ? item : null);
        var text = value is null ? "" : XDataV2.FormatValue(value);
        return caption + " <" + (text.Length == 0 ? "пусто" : text) + ">";
    }
}
