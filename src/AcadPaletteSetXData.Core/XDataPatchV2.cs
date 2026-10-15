using System;
using System.Collections.Generic;
using System.Linq;
using Node = AcadPaletteSetXData.Core.XDataV2.Tokens.Node;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// XDATA v2 (TZXDATA 6): an edit changes one pair, or one row of a <c>Table</c>; every other value,
/// unknown values and other RegApps are kept in their order. Values are converted to the schema type.
/// </summary>
public sealed partial class XDataPatch
{
    private readonly SchemaSnapshot? schema;
    private readonly ParameterTypeRegistry types;
    private readonly Func<Guid> newGuid;

    public XDataPatch() : this(null) { }

    /// <param name="schema">Schema of the drawing; required for v2 edits.</param>
    /// <param name="newGuid">Source of <c>DEVICE_ID</c> values (tests pass a fixed sequence).</param>
    public XDataPatch(SchemaSnapshot? schema, ParameterTypeRegistry? types = null, Func<Guid>? newGuid = null)
    {
        this.schema = schema;
        this.types = types ?? ParameterTypeRegistry.Default;
        this.newGuid = newGuid ?? Guid.NewGuid;
    }

    private EntityDataSnapshot ApplyV2(EntityDataSnapshot snapshot, SelectionEdit edit, ObjectValues? values)
    {
        var schema = this.schema?.Schema != null ? this.schema : throw new InvalidOperationException("Схема параметров не загружена; выполните VELEC_SCHEMA_IMPORT.");
        var xdata = snapshot.XData;
        var app = XDataV2.FindApp(xdata);
        var format = values != null && ReferenceEquals(values.XData, xdata) ? values.Format : XDataV2.FormatOf(xdata);
        var name = "Объект " + snapshot.Handle + ": ";
        if (format == XDataFormat.V1) throw new InvalidOperationException(name + "XDATA в формате v1; сначала выполните миграцию в v2.");
        if (format == XDataFormat.Newer) throw new InvalidOperationException(name + "XDATA записан более новой версией формата; только чтение.");
        if (app != null && app.Value.Others) throw new InvalidOperationException(name + "XDATA " + DrawingNames.RegApp + " повторяется; запись отменена.");

        // An empty section (only its 1001) gets the format version, as a new one.
        var header = app != null && format != XDataFormat.None;
        List<Node> nodes;
        if (!header) nodes = new List<Node>();
        else
        {
            nodes = XDataV2.Tokens.ReadPairs(xdata, app!.Value.Start + 2, app.Value.End, out var damaged);
            if (damaged) throw new InvalidOperationException(name + "незакрытый или лишний список XDATA; запись отменена.");
        }

        switch (edit.Kind)
        {
            case SelectionEditKind.Parameter: SetParameter(nodes, edit, schema); break;
            case SelectionEditKind.RemoveParameter:
                if (ParameterSchema.ReservedIds.Contains(edit.Field, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Системный Id " + edit.Field + " не удаляется этой операцией.", nameof(edit));
                nodes.RemoveAll(node => ParameterSchema.SameId(node.Id, edit.Field));
                break;
            case SelectionEditKind.TableCell:
            case SelectionEditKind.AddTableRow:
            case SelectionEditKind.DeleteTableRow: EditTable(nodes, edit, schema, name); break;
            case SelectionEditKind.Template:
                if (edit.Field.Length == 0) nodes.RemoveAll(node => ParameterSchema.SameId(node.Id, XDataV2.TemplateKey));
                else
                {
                    if (!schema.Templates.TryGetValue(edit.Field, out var template))
                        throw new InvalidOperationException("Шаблон " + edit.Field + " не описан в схеме.");
                    // Changing the template keeps all values (TZXDATA 6.6); a new object gets its DEVICE_ID (6.4).
                    if (!nodes.Any(node => ParameterSchema.SameId(node.Id, XDataV2.DeviceIdKey)))
                        SetSystem(nodes, XDataV2.DeviceIdKey, XDataV2.NewDeviceId(newGuid()));
                    SetSystem(nodes, XDataV2.TemplateKey, template.Id!);
                }
                break;
            case SelectionEditKind.NewDeviceId: SetSystem(nodes, XDataV2.DeviceIdKey, XDataV2.NewDeviceId(newGuid())); break;
            default: throw new ArgumentException("Неизвестная операция.", nameof(edit));
        }

        if (!header && nodes.Count == 0) return snapshot;
        var result = new List<DataValue>(xdata.Count + 8);
        var start = app?.Start ?? xdata.Count;
        for (var i = 0; i < start; i++) result.Add(xdata[i]);
        result.Add(app is null ? new DataValue(1001, DrawingNames.RegApp) : xdata[start]);
        result.Add(header ? xdata[start + 1] : new DataValue(1070, (short)XDataV2.FormatVersion));
        foreach (var node in nodes) result.AddRange(node.All);
        for (var i = app?.End ?? xdata.Count; i < xdata.Count; i++) result.Add(xdata[i]);
        // Records are kept by reference: the transaction writes only the XRecords that changed.
        return new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, result, snapshot.Records, snapshot.Warning);
    }

    private void SetParameter(List<Node> nodes, SelectionEdit edit, SchemaSnapshot schema)
    {
        if (ParameterSchema.ReservedIds.Contains(edit.Field, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Системный Id " + edit.Field + " задаётся отдельной операцией.", nameof(edit));
        var (parameter, type) = Scalar(edit.Field, schema);
        var value = Convert(parameter, type, edit.Data!, schema);
        Set(nodes, Pair(parameter.Id!, type.ToXData(value)));
    }

    private void EditTable(List<Node> nodes, SelectionEdit edit, SchemaSnapshot schema, string name)
    {
        if (!schema.Parameters.TryGetValue(edit.Table, out var table) || types.Find(table.Type)?.Kind != ParameterKind.Table)
            throw new InvalidOperationException("Параметр " + edit.Table + " не является таблицей схемы.");
        var key = XDataV2.KeyColumn(table, schema, types) ?? throw new InvalidOperationException("У таблицы " + table.Id + " нет ключевой колонки.");
        var index = nodes.FindIndex(node => ParameterSchema.SameId(node.Id, table.Id));
        var stored = index < 0 ? null : nodes[index].Value;
        if (stored != null && !(stored.Count >= 2 && XDataV2.Tokens.IsBracket(stored[0], "{") && XDataV2.Tokens.IsBracket(stored[stored.Count - 1], "}")))
            throw new InvalidOperationException(name + "значение " + table.Id + " не является списком строк Table; удалите его или исправьте.");
        var items = stored is null ? new List<XDataV2.Tokens.RowItem>() : XDataV2.Tokens.ReadRows(stored);
        bool Matches(XDataV2.Tokens.RowItem item, string rowKey) => item.Row != null && RowKey(item.Row, key) == rowKey;

        switch (edit.Kind)
        {
            case SelectionEditKind.DeleteTableRow:
                if (stored is null) return;
                items.RemoveAll(item => Matches(item, edit.RowKey));
                break;
            case SelectionEditKind.AddTableRow:
            {
                var cells = new List<Node>();
                foreach (var cell in edit.Cells) Set(cells, Cell(table, cell.Key, cell.Value, schema));
                var rowKey = RowKey(cells, key) ?? throw new ArgumentException("Новая строка " + table.Id + " должна содержать ключ " + key + ".", nameof(edit));
                if (items.Any(item => Matches(item, rowKey)))
                    throw new InvalidOperationException(name + "строка " + table.Id + " с ключом \"" + rowKey + "\" уже есть.");
                items.Add(Row(cells));
                break;
            }
            default:
            {
                var cell = Cell(table, edit.Field, edit.Data!, schema);
                var found = false;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Matches(items[i], edit.RowKey)) continue;
                    var cells = items[i].Row!;
                    Set(cells, cell);
                    items[i] = Row(cells);
                    found = true;
                }
                if (!found)
                {
                    // A row missing on some objects of the group is added, as in group editing of materials.
                    var cells = new List<Node>();
                    if (!ParameterSchema.SameId(edit.Field, key)) cells.Add(Cell(table, key, edit.RowKey, schema));
                    cells.Add(cell);
                    items.Add(Row(cells));
                }
                break;
            }
        }

        var values = new List<DataValue> { new DataValue(1002, "{") };
        foreach (var item in items) values.AddRange(item.All);
        values.Add(new DataValue(1002, "}"));
        var all = new List<DataValue> { new DataValue(1000, index < 0 ? table.Id! : nodes[index].Id!) };
        all.AddRange(values);
        Set(nodes, new Node(all[0].Value as string, all, 1));
    }

    private Node Cell(SchemaParameter table, string column, object value, SchemaSnapshot schema)
    {
        if (table.Columns?.Any(id => ParameterSchema.SameId(id, column)) != true)
            throw new InvalidOperationException(column + " не является колонкой таблицы " + table.Id + ".");
        var (parameter, type) = Scalar(column, schema);
        return Pair(parameter.Id!, type.ToXData(Convert(parameter, type, value, schema)));
    }

    private (SchemaParameter Parameter, IParameterType Type) Scalar(string id, SchemaSnapshot schema)
    {
        if (!schema.Parameters.TryGetValue(id, out var parameter))
            throw new InvalidOperationException("Параметр " + id + " не описан в схеме; доступно только удаление.");
        var type = types.Find(parameter.Type) ?? throw new InvalidOperationException("Тип \"" + parameter.Type + "\" параметра " + id + " не поддерживается.");
        if (type.Kind == ParameterKind.Table) throw new InvalidOperationException("Таблица " + id + " изменяется по строкам.");
        if (type.Kind == ParameterKind.CalculatedString) throw new InvalidOperationException("Параметр " + id + " вычисляется (CalculatedString) и не задаётся вручную.");
        return (parameter, type);
    }

    private static object Convert(SchemaParameter parameter, IParameterType type, object value, SchemaSnapshot schema)
    {
        var error = type.Convert(value, out var converted) ?? type.CheckValue(Json(converted), parameter, schema.Schema!);
        if (error != null) throw new InvalidOperationException("Параметр " + parameter.Id + ": " + error + ".");
        return converted;
    }

    private static JsonValue Json(object value) => value switch
    {
        string text => new JsonString(text),
        bool flag => JsonBoolean.From(flag),
        int number => new JsonNumber(number),
        double number => new JsonNumber(number),
        _ => JsonNull.Instance
    };

    private static string? RowKey(IEnumerable<Node> cells, string key)
    {
        var cell = cells.FirstOrDefault(node => ParameterSchema.SameId(node.Id, key));
        return cell != null && cell.Value.Count == 1 ? XDataV2.FormatValue(cell.Value[0].Value) : null;
    }

    private static XDataV2.Tokens.RowItem Row(List<Node> cells)
    {
        var all = new List<DataValue> { new DataValue(1002, "{") };
        foreach (var cell in cells) all.AddRange(cell.All);
        all.Add(new DataValue(1002, "}"));
        return new XDataV2.Tokens.RowItem(all, cells);
    }

    private static Node Pair(string id, DataValue value) => new Node(id, new List<DataValue> { new DataValue(1000, id), value }, 1);

    /// <summary>System pairs go first, after <c>FMT</c>, as in TZXDATA 6.1.</summary>
    private static void SetSystem(List<Node> nodes, string id, string value)
    {
        var pair = Pair(id, new DataValue(1000, value));
        if (nodes.Any(node => ParameterSchema.SameId(node.Id, id))) { Set(nodes, pair); return; }
        var at = id == XDataV2.TemplateKey ? nodes.FindIndex(node => ParameterSchema.SameId(node.Id, XDataV2.DeviceIdKey)) + 1 : 0;
        nodes.Insert(at, pair);
    }

    /// <summary>Replaces the first pair with the same Id (keeping its spelling) and drops repeated ones, or appends.</summary>
    private static void Set(List<Node> nodes, Node pair)
    {
        var index = nodes.FindIndex(node => ParameterSchema.SameId(node.Id, pair.Id));
        if (index < 0) { nodes.Add(pair); return; }
        var all = new List<DataValue> { nodes[index].All[0] };
        all.AddRange(pair.Value);
        nodes[index] = new Node(nodes[index].Id, all, 1);
        for (var i = nodes.Count - 1; i > index; i--)
            if (ParameterSchema.SameId(nodes[i].Id, pair.Id)) nodes.RemoveAt(i);
    }
}
