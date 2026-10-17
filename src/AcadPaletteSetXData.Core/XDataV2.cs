using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>Format of the <see cref="DrawingNames.RegApp"/> XDATA of an object (TZXDATA 6.1).</summary>
public enum XDataFormat
{
    /// <summary>The object has no <see cref="DrawingNames.RegApp"/> XDATA, or only its empty <c>1001</c>.</summary>
    None,
    /// <summary>Strings <c>key=value</c>: no <c>(1070 . 2)</c> first; read by <see cref="XDataParser"/>.</summary>
    V1,
    /// <summary>Pairs <c>Id</c> + typed value.</summary>
    V2,
    /// <summary><c>(1070 . N)</c> with N &gt; 2: written by a newer plugin, read-only.</summary>
    Newer
}

/// <summary>How a stored value relates to the schema (TZXDATA 6.5).</summary>
public enum ObjectValueState
{
    /// <summary>Typed by the schema.</summary>
    Typed,
    /// <summary><c>Id</c> is not in NOD: shown as a string in «Не описаны в схеме», can be removed.</summary>
    NotInSchema,
    /// <summary>The stored group code or value does not fit the schema type: shown as a string.</summary>
    TypeMismatch,
    /// <summary>The <c>Enum</c> key is not in the enum: the key is shown.</summary>
    UnknownEnumKey,
    /// <summary>The schema type is unknown to this plugin version: a read-only string (TZXDATA 11).</summary>
    UnknownType
}

/// <summary>A value of one parameter, or a cell of a <c>Table</c> row.</summary>
public sealed class ObjectValue
{
    internal ObjectValue(string id, SchemaParameter? parameter, IParameterType? type, ObjectValueState state, object value,
        IReadOnlyList<DataValue> stored, IReadOnlyList<ObjectRow>? rows, string? warning)
    {
        Id = id; Parameter = parameter; Type = type; State = state; Value = value; Stored = stored; Rows = rows; Warning = warning;
    }

    /// <summary>The <c>Id</c> as written in XDATA.</summary>
    public string Id { get; }
    public SchemaParameter? Parameter { get; }
    public IParameterType? Type { get; }
    public ObjectValueState State { get; }
    /// <summary>
    /// <see cref="ObjectValueState.Typed"/> and <see cref="ObjectValueState.UnknownEnumKey"/>: <c>string</c>, <c>double</c>,
    /// <c>int</c> or <c>bool</c>; a table: its rows; otherwise the stored value as a string.
    /// </summary>
    public object Value { get; }
    /// <summary>Stored group codes and values of the value (a list with its brackets for a table).</summary>
    public IReadOnlyList<DataValue> Stored { get; }
    /// <summary>Rows of a <c>Table</c> value; null for scalars.</summary>
    public IReadOnlyList<ObjectRow>? Rows { get; }
    public string? Warning { get; }
    public bool IsTable => Rows != null;
    /// <summary>The value in the invariant culture (stage 5 formats it by <c>Editor</c>).</summary>
    public string Text => IsTable ? "Строк: " + Rows!.Count : XDataV2.FormatValue(Value);
}

/// <summary>A row of a <c>Table</c>: cells are pairs <c>Id</c> + value, as at the top level.</summary>
public sealed class ObjectRow
{
    internal ObjectRow(IReadOnlyList<ObjectValue> cells, string? key) { Cells = cells; Key = key; }
    public IReadOnlyList<ObjectValue> Cells { get; }
    /// <summary>Text of the key column (<c>Key</c>, or the first <c>String</c> column) that matches rows in group editing.</summary>
    public string? Key { get; }
    public ObjectValue? Find(string id) => Cells.FirstOrDefault(cell => ParameterSchema.SameId(cell.Id, id));
}

/// <summary>Values of one object read from <see cref="DrawingNames.RegApp"/> XDATA v2 (TZXDATA 10.2).</summary>
public sealed class ObjectValues
{
    private readonly Dictionary<string, ObjectValue> byId;

    internal ObjectValues(EntityDataSnapshot snapshot, SchemaSnapshot schema, XDataFormat format, string? deviceId, string? template,
        IReadOnlyList<ObjectValue> values, IReadOnlyList<string> warnings, int size)
    {
        Handle = snapshot.Handle; EntityType = snapshot.EntityType; XData = snapshot.XData; Schema = schema;
        Format = format; DeviceId = deviceId; Template = template; Values = values; Warnings = warnings; XDataSize = size;
        byId = new Dictionary<string, ObjectValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) if (!byId.ContainsKey(value.Id)) byId.Add(value.Id, value);
    }

    public string Handle { get; }
    public string EntityType { get; }
    /// <summary>The parsed XDATA; the cache compares it with a fresh read instead of parsing again.</summary>
    public IReadOnlyList<DataValue> XData { get; }
    /// <summary>The schema the values were typed with.</summary>
    public SchemaSnapshot Schema { get; }
    public XDataFormat Format { get; }
    public string? DeviceId { get; }
    public string? Template { get; }
    /// <summary>Parameter values in XDATA order, without <c>FMT</c>, <c>DEVICE_ID</c> and <c>TEMPLATE</c>.</summary>
    public IReadOnlyList<ObjectValue> Values { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Estimated size of the whole XDATA of the object, all RegApps (TZXDATA 6.3).</summary>
    public int XDataSize { get; }

    public ObjectValue? Find(string id) => byId.TryGetValue(id, out var value) ? value : null;
}

/// <summary>A <c>DEVICE_ID</c> found on several objects of a selection (TZXDATA 6.4).</summary>
public sealed class DeviceIdDuplicate
{
    internal DeviceIdDuplicate(string deviceId, string original, IReadOnlyList<string> copies) { DeviceId = deviceId; Original = original; Copies = copies; }
    public string DeviceId { get; }
    /// <summary>The object with the smallest handle keeps the ID.</summary>
    public string Original { get; }
    /// <summary>Objects that need a new GUID (<c>COPY</c>, <c>ARRAY</c>, clipboard).</summary>
    public IReadOnlyList<string> Copies { get; }
}

/// <summary>Reads and writes <see cref="DrawingNames.RegApp"/> XDATA v2 (TZXDATA 6).</summary>
public static class XDataV2
{
    public const int FormatVersion = 2;
    public const string DeviceIdKey = "DEVICE_ID";
    public const string TemplateKey = "TEMPLATE";
    public const string FormatKey = "FMT";
    /// <summary>Total XDATA of an object, all RegApps (TZXDATA 6.3; AutoCAD limit is 16 KB).</summary>
    public const int MaxXDataBytes = 16383;

    /// <summary>Format of the <see cref="DrawingNames.RegApp"/> values of an object.</summary>
    public static XDataFormat FormatOf(IReadOnlyList<DataValue> xdata)
    {
        var app = FindApp(xdata);
        if (app is null) return XDataFormat.None;
        return FormatOf(xdata, app.Value.Start);
    }

    /// <summary>Parses the values of one object; never throws on damaged data, the problems become warnings.</summary>
    public static ObjectValues Read(EntityDataSnapshot snapshot, SchemaSnapshot schema, ParameterTypeRegistry? types = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (schema is null) throw new ArgumentNullException(nameof(schema));
        types ??= ParameterTypeRegistry.Default;
        var warnings = new List<string>();
        var values = new List<ObjectValue>();
        string? deviceId = null, template = null;
        var xdata = snapshot.XData;
        var size = EstimateSize(xdata);
        if (size > MaxXDataBytes) warnings.Add("Размер XDATA объекта превышает " + MaxXDataBytes + " байт.");
        var app = FindApp(xdata);
        var format = app is null ? XDataFormat.None : FormatOf(xdata, app.Value.Start);
        if (format == XDataFormat.Newer) warnings.Add("XDATA " + DrawingNames.RegApp + " записан более новой версией формата; только чтение.");
        if (app != null && app.Value.Others) warnings.Add("XDATA " + DrawingNames.RegApp + " повторяется; используется первый.");
        if (format == XDataFormat.V2)
        {
            var nodes = Tokens.ReadPairs(xdata, app!.Value.Start + 2, app.Value.End, out var damaged);
            if (damaged) warnings.Add("Незакрытый или лишний список XDATA " + DrawingNames.RegApp + ".");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                if (node.Id is null) { warnings.Add("Значение XDATA без Id пропущено."); continue; }
                if (!seen.Add(node.Id)) { warnings.Add(node.Id + ": повторяется, используется первое значение."); continue; }
                if (node.Value.Count == 0) { warnings.Add(node.Id + ": нет значения."); continue; }
                if (ParameterSchema.SameId(node.Id, DeviceIdKey))
                {
                    deviceId = SystemText(node, warnings);
                    if (deviceId != null && !Guid.TryParse(deviceId, out _)) warnings.Add(DeviceIdKey + ": \"" + deviceId + "\" не является GUID.");
                }
                else if (ParameterSchema.SameId(node.Id, TemplateKey))
                {
                    template = SystemText(node, warnings);
                    if (template != null && schema.Schema != null && !schema.Templates.ContainsKey(template))
                        warnings.Add(TemplateKey + ": шаблон \"" + template + "\" не описан в схеме.");
                }
                else if (ParameterSchema.SameId(node.Id, FormatKey)) warnings.Add(FormatKey + ": зарезервированный Id пропущен.");
                else
                {
                    var value = Value(node, schema, types, null);
                    values.Add(value);
                    if (value.Warning != null) warnings.Add(value.Id + ": " + value.Warning);
                    if (value.Rows != null)
                        foreach (var cell in value.Rows.SelectMany(row => row.Cells).Where(cell => cell.Warning != null))
                            warnings.Add(value.Id + "/" + cell.Id + ": " + cell.Warning);
                }
            }
        }
        return new ObjectValues(snapshot, schema, format, deviceId, template, values, warnings.Distinct().ToArray(), size);
    }

    /// <summary>
    /// Estimated size of XDATA as AutoCAD counts it for the 16 KB limit: a group code and the data of each value,
    /// strings with a length prefix. The estimate errs on the large side so that a write is refused, not truncated.
    /// </summary>
    public static int EstimateSize(IReadOnlyList<DataValue> xdata)
    {
        long size = 0;
        foreach (var value in xdata)
        {
            size += 2;
            switch (value.Value)
            {
                case string text: size += 3 + Encoding.UTF8.GetByteCount(text); break;
                case byte[] bytes: size += 1 + bytes.Length; break;
                case short _: size += 2; break;
                case int _: size += 4; break;
                case double _: size += 8; break;
                default:
                    // Points (1010–1033) and handles (1005) are the largest remaining values.
                    size += value.TypeCode >= 1010 && value.TypeCode <= 1033 ? 24 : 8; break;
            }
        }
        return size > int.MaxValue ? int.MaxValue : (int)size;
    }

    /// <summary>
    /// Duplicates of <c>DEVICE_ID</c> in a selection: the object with the smallest handle keeps the ID (TZXDATA 6.4).
    /// </summary>
    public static IReadOnlyList<DeviceIdDuplicate> FindDuplicateDeviceIds(IEnumerable<ObjectValues> objects) =>
        objects.Where(item => !string.IsNullOrEmpty(item.DeviceId))
            .GroupBy(item => item.DeviceId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group =>
            {
                var handles = group.Select(item => item.Handle).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(HandleOrder).ThenBy(h => h, StringComparer.Ordinal).ToArray();
                return new DeviceIdDuplicate(group.Key, handles[0], handles.Skip(1).ToArray());
            })
            .OrderBy(duplicate => HandleOrder(duplicate.Original)).ToArray();

    /// <summary>Text of a stored value in the invariant culture.</summary>
    public static string FormatValue(object value) => value switch
    {
        bool flag => flag ? "1" : "0",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => BitConverter.ToString(bytes).Replace("-", ""),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>The new <c>DEVICE_ID</c> text: a GUID in the <c>D</c> format, upper case, as in TZXDATA 6.1.</summary>
    public static string NewDeviceId(Guid guid) => guid.ToString("D").ToUpperInvariant();

    internal static ulong HandleOrder(string handle) =>
        ulong.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) ? number : ulong.MaxValue;

    /// <summary>The first <see cref="DrawingNames.RegApp"/> section: index of its 1001 and the end (exclusive).</summary>
    internal static (int Start, int End, bool Others)? FindApp(IReadOnlyList<DataValue> xdata)
    {
        for (var index = 0; index < xdata.Count; index++)
        {
            if (!IsApp(xdata[index])) continue;
            var end = index + 1;
            while (end < xdata.Count && xdata[end].TypeCode != 1001) end++;
            var others = false;
            for (var next = end; next < xdata.Count; next++) others |= IsApp(xdata[next]);
            return (index, end, others);
        }
        return null;
    }

    internal static bool IsApp(DataValue value) =>
        value.TypeCode == 1001 && value.Value is string app && string.Equals(app, DrawingNames.RegApp, StringComparison.OrdinalIgnoreCase);

    private static XDataFormat FormatOf(IReadOnlyList<DataValue> xdata, int start)
    {
        if (start + 1 >= xdata.Count || xdata[start + 1].TypeCode == 1001) return XDataFormat.None;
        if (xdata[start + 1].TypeCode != 1070) return XDataFormat.V1;
        var version = xdata[start + 1].Value switch { short s => s, int n => n, long l => l, _ => 0L };
        return version == FormatVersion ? XDataFormat.V2 : version > FormatVersion ? XDataFormat.Newer : XDataFormat.V1;
    }

    private static string? SystemText(Tokens.Node node, List<string> warnings)
    {
        if (node.Value.Count == 1 && node.Value[0].TypeCode == 1000 && node.Value[0].Value is string text) return text;
        warnings.Add(node.Id + ": ожидается строка (1000).");
        return null;
    }

    private static ObjectValue Value(Tokens.Node node, SchemaSnapshot schema, ParameterTypeRegistry types, SchemaParameter? table)
    {
        var id = node.Id!;
        schema.Parameters.TryGetValue(id, out var parameter);
        var stored = node.Value;
        var list = Tokens.IsBracket(stored[0], "{");
        if (parameter is null)
            return Untyped(id, null, null, ObjectValueState.NotInSchema, stored, "не описан в схеме");
        if (table != null && table.Columns?.Any(column => ParameterSchema.SameId(column, id)) != true)
            return Untyped(id, parameter, null, ObjectValueState.NotInSchema, stored, "не является колонкой " + table.Id);
        var type = types.Find(parameter.Type);
        if (type is null)
            return Untyped(id, parameter, null, ObjectValueState.UnknownType, stored, "тип \"" + parameter.Type + "\" не поддерживается; только чтение");
        if (type.Kind == ParameterKind.Table)
        {
            if (!list || table != null) return Untyped(id, parameter, type, ObjectValueState.TypeMismatch, stored, "ожидается список строк Table");
            return TableValue(id, parameter, type, stored, schema, types);
        }
        if (list || stored.Count != 1 || !type.TryRead(stored[0], out var typed))
            return Untyped(id, parameter, type, ObjectValueState.TypeMismatch, stored,
                "тип значения не совпадает со схемой: ожидается " + type.Name + " (" + type.XDataCode + "), записано " + Codes(stored));
        if (type.Kind == ParameterKind.Enum && parameter.Editor?.AllowCustom != true)
        {
            var reference = schema.Schema?.FindEnum(parameter.Enum);
            if (reference != null && reference.FindItem((string)typed) is null)
                return new ObjectValue(id, parameter, type, ObjectValueState.UnknownEnumKey, typed, stored, null,
                    "ключ \"" + typed + "\" отсутствует в справочнике " + reference.Id);
        }
        return new ObjectValue(id, parameter, type, ObjectValueState.Typed, typed, stored, null, null);
    }

    private static ObjectValue TableValue(string id, SchemaParameter parameter, IParameterType type, IReadOnlyList<DataValue> stored,
        SchemaSnapshot schema, ParameterTypeRegistry types)
    {
        var rows = new List<ObjectRow>();
        string? warning = null;
        var key = KeyColumn(parameter, schema, types);
        foreach (var item in Tokens.ReadRows(stored))
        {
            if (item.Row is null) { warning ??= "значение вне строки таблицы пропущено"; continue; }
            var cells = new List<ObjectValue>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in item.Row)
            {
                if (cell.Id is null || cell.Value.Count == 0) { warning ??= "ячейка без Id или значения пропущена"; continue; }
                if (!seen.Add(cell.Id)) { warning ??= "колонка " + cell.Id + " повторяется в строке"; continue; }
                cells.Add(Value(cell, schema, types, parameter));
            }
            var keyCell = key is null ? null : cells.FirstOrDefault(cell => ParameterSchema.SameId(cell.Id, key));
            rows.Add(new ObjectRow(cells, keyCell is null || keyCell.IsTable ? null : FormatValue(keyCell.Value)));
        }
        return new ObjectValue(id, parameter, type, ObjectValueState.Typed, rows, stored, rows, warning);
    }

    /// <summary>Column matching rows in group editing: <c>Key</c>, or the first <c>String</c> column (TZXDATA 4.2).</summary>
    public static string? KeyColumn(SchemaParameter table, SchemaSnapshot schema, ParameterTypeRegistry? types = null)
    {
        types ??= ParameterTypeRegistry.Default;
        if (!string.IsNullOrEmpty(table.Key)) return table.Key;
        return table.Columns?.FirstOrDefault(column =>
            schema.Parameters.TryGetValue(column, out var parameter) && types.Find(parameter.Type)?.Kind == ParameterKind.String);
    }

    private static ObjectValue Untyped(string id, SchemaParameter? parameter, IParameterType? type, ObjectValueState state,
        IReadOnlyList<DataValue> stored, string warning)
    {
        // A list outside the schema is shown by its rows of raw cells.
        if (Tokens.IsBracket(stored[0], "{") && stored.Count > 1)
        {
            var rows = Tokens.ReadRows(stored).Where(item => item.Row != null).Select(item => new ObjectRow(item.Row!
                .Where(cell => cell.Id != null && cell.Value.Count != 0)
                .Select(cell => Untyped(cell.Id!, null, null, ObjectValueState.NotInSchema, cell.Value, "не описан в схеме")).ToArray(), null)).ToArray();
            return new ObjectValue(id, parameter, type, state, rows, stored, rows, warning);
        }
        var text = string.Join(" ", stored.Select(value => FormatValue(value.Value)));
        return new ObjectValue(id, parameter, type, state, text, stored, null, warning);
    }

    private static string Codes(IReadOnlyList<DataValue> stored) =>
        Tokens.IsBracket(stored[0], "{") ? "список" : string.Join(", ", stored.Select(value => value.TypeCode.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Lossless split of v2 values into pairs; unknown values are kept as nodes without an Id.</summary>
    internal static class Tokens
    {
        internal sealed class Node
        {
            public Node(string? id, List<DataValue> all, int valueStart)
            {
                Id = id; All = all;
                var value = new DataValue[all.Count - valueStart];
                all.CopyTo(valueStart, value, 0, value.Length);
                Value = value;
            }
            /// <summary>Null for a value that is not a pair (kept as is).</summary>
            public string? Id { get; }
            /// <summary>All values of the node: the Id and its value.</summary>
            public List<DataValue> All { get; }
            public IReadOnlyList<DataValue> Value { get; }
        }

        internal sealed class RowItem
        {
            public RowItem(List<DataValue> all, List<Node>? row) { All = all; Row = row; }
            public List<DataValue> All { get; }
            public List<Node>? Row { get; }
        }

        public static bool IsBracket(DataValue value, string bracket) => value.TypeCode == 1002 && value.Value is string text && text == bracket;

        /// <summary>Pairs from <paramref name="start"/> to <paramref name="end"/>; a stray <c>}</c> is a node without an Id.</summary>
        public static List<Node> ReadPairs(IReadOnlyList<DataValue> values, int start, int end, out bool damaged)
        {
            damaged = false;
            var nodes = new List<Node>();
            var i = start;
            while (i < end)
            {
                if (values[i].TypeCode == 1000 && values[i].Value is string id)
                {
                    var next = i + 1 < end && !IsBracket(values[i + 1], "}") ? Skip(values, i + 1, end, ref damaged) : i + 1;
                    nodes.Add(new Node(id, Range(values, i, next), 1));
                    i = next;
                }
                else
                {
                    if (IsBracket(values[i], "}")) damaged = true;
                    var next = IsBracket(values[i], "}") ? i + 1 : Skip(values, i, end, ref damaged);
                    nodes.Add(new Node(null, Range(values, i, next), 0));
                    i = next;
                }
            }
            return nodes;
        }

        /// <summary>Rows of a table value <c>{ { pairs } { pairs } }</c>; other values are items without a row.</summary>
        public static List<RowItem> ReadRows(IReadOnlyList<DataValue> list)
        {
            var items = new List<RowItem>();
            var end = list.Count > 1 && IsBracket(list[list.Count - 1], "}") ? list.Count - 1 : list.Count;
            var damaged = false;
            var i = 1;
            while (i < end)
            {
                var next = Skip(list, i, end, ref damaged);
                var all = Range(list, i, next);
                var closed = next - i >= 2 && IsBracket(list[next - 1], "}");
                items.Add(new RowItem(all, IsBracket(list[i], "{") ? ReadPairs(list, i + 1, closed ? next - 1 : next, out _) : null));
                i = next;
            }
            return items;
        }

        /// <summary>Index after one value, or after a whole list with nested lists.</summary>
        public static int Skip(IReadOnlyList<DataValue> values, int i, int end, ref bool damaged)
        {
            if (!IsBracket(values[i], "{")) return i + 1;
            var level = 0;
            do
            {
                if (IsBracket(values[i], "{")) level++;
                else if (IsBracket(values[i], "}")) level--;
                i++;
            } while (i < end && level > 0);
            if (level > 0) damaged = true;
            return i;
        }

        private static List<DataValue> Range(IReadOnlyList<DataValue> values, int start, int end)
        {
            var result = new List<DataValue>(end - start);
            for (var i = start; i < end; i++) result.Add(values[i]);
            return result;
        }
    }
}
