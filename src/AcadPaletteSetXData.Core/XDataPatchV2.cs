using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Node = AcadPaletteSetXData.Core.XDataV2.Tokens.Node;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// XDATA v2 (TZXDATA 6): an edit changes one pair, or one row of a <c>Table</c>; every other value,
/// unknown values and other RegApps are kept in their order. Values are converted to the schema type.
/// <c>Persist</c> results of <c>CalculatedString</c> that depend on the change are written by the same patch (TZXDATA 7.4).
/// </summary>
public sealed partial class XDataPatch
{
    private readonly SchemaSnapshot? schema;
    private readonly ParameterTypeRegistry types;
    private readonly Func<Guid> newGuid;
    private readonly CultureInfo? culture;

    public XDataPatch() : this(null) { }

    /// <param name="schema">Schema of the drawing; required for v2 edits.</param>
    /// <param name="newGuid">Source of <c>DEVICE_ID</c> values (tests pass a fixed sequence).</param>
    /// <param name="culture">Decimal separator of <c>Persist</c> results; the current culture by default (TZXDATA 7.2).</param>
    public XDataPatch(SchemaSnapshot? schema, ParameterTypeRegistry? types = null, Func<Guid>? newGuid = null, CultureInfo? culture = null)
    {
        this.schema = schema;
        this.types = types ?? ParameterTypeRegistry.Default;
        this.newGuid = newGuid ?? Guid.NewGuid;
        this.culture = culture;
    }

    private EntityDataSnapshot ApplyV2(EntityDataSnapshot snapshot, SelectionEdit edit, ObjectValues? values)
    {
        var schema = this.schema?.Schema != null ? this.schema : throw new InvalidOperationException("Схема параметров не загружена; выполните VELEC_SCHEMA_IMPORT.");
        var xdata = snapshot.XData;
        var app = XDataV2.FindApp(xdata);
        var format = values != null && ReferenceEquals(values.XData, xdata) ? values.Format : XDataV2.FormatOf(xdata);
        var name = "Объект " + snapshot.Handle + ": ";
        // The recalculation goes over a whole drawing: objects without v2 values are not errors.
        if (edit.Kind == SelectionEditKind.RecalculatePersisted && format != XDataFormat.V2) return snapshot;
        // Only objects with a template have defaults (TZXDATA 6.2, 6.6).
        if (edit.Kind == SelectionEditKind.CommitDefaults && format == XDataFormat.None) return snapshot;
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

        // Persist results are recalculated from the cached values of the object, without parsing XDATA again.
        var cached = values != null && ReferenceEquals(values.XData, xdata) && ReferenceEquals(values.Schema, schema) ? values : null;
        ObjectCalculation Calculation() => new ObjectCalculation(cached ?? XDataV2.Read(snapshot, schema, types), culture, types);
        IReadOnlyList<CalculatedValue>? recalculated = null;
        ObjectCalculation? calculation = null;

        switch (edit.Kind)
        {
            case SelectionEditKind.Parameter:
            {
                var (id, value) = SetParameter(nodes, edit, schema);
                if (schema.Formulas.Dependents(id).Any(formula => formula.Persist))
                    recalculated = (calculation = Calculation()).SetValue(id, value);
                break;
            }
            case SelectionEditKind.RemoveParameter:
                if (ParameterSchema.ReservedIds.Contains(edit.Field, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Системный Id " + edit.Field + " не удаляется этой операцией.", nameof(edit));
                nodes.RemoveAll(node => ParameterSchema.SameId(node.Id, edit.Field));
                if (schema.Formulas.Find(edit.Field) is null && schema.Formulas.Dependents(edit.Field).Any(formula => formula.Persist))
                    recalculated = (calculation = Calculation()).SetValue(edit.Field, null);
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
                    // EntityTypes filters the objects of a template (TZXDATA 4.4); a wrong one in a group cancels the operation.
                    if (!template.Accepts(snapshot.EntityType))
                        throw new InvalidOperationException(name + "шаблон " + template.Id + " не применяется к типу " + snapshot.EntityType +
                            " (" + string.Join(", ", template.EntityTypes!) + ").");
                    // Changing the template keeps all values (TZXDATA 6.6); a new object gets its DEVICE_ID (6.4).
                    if (!nodes.Any(node => ParameterSchema.SameId(node.Id, XDataV2.DeviceIdKey)))
                        SetSystem(nodes, XDataV2.DeviceIdKey, XDataV2.NewDeviceId(newGuid()));
                    SetSystem(nodes, XDataV2.TemplateKey, template.Id!);
                }
                // Defaults of the template take part in the formulas.
                if (schema.Formulas.Formulas.Any(formula => formula.Persist))
                    recalculated = (calculation = Calculation()).SetTemplate(edit.Field.Length == 0 ? null : schema.Templates[edit.Field].Id);
                break;
            case SelectionEditKind.NewDeviceId: SetSystem(nodes, XDataV2.DeviceIdKey, XDataV2.NewDeviceId(newGuid())); break;
            case SelectionEditKind.RecalculatePersisted:
                calculation = Calculation();
                recalculated = calculation.Results;
                break;
            case SelectionEditKind.CommitDefaults:
            {
                calculation = Calculation();
                var missing = calculation.MissingDefaults();
                CommitDefaults(nodes, calculation, missing, schema);
                // Only missing results are written: a stale stored one is updated by its sources or by VELEC_CHECK (TZXDATA 7.4).
                recalculated = calculation.Results.Where(result => missing.Contains(result.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
                break;
            }
            default: throw new ArgumentException("Неизвестная операция.", nameof(edit));
        }
        if (calculation != null && recalculated != null)
            WritePersisted(nodes, recalculated, edit.Kind == SelectionEditKind.Parameter, calculation.Template, schema, name);

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

    private (string Id, object Value) SetParameter(List<Node> nodes, SelectionEdit edit, SchemaSnapshot schema)
    {
        if (ParameterSchema.ReservedIds.Contains(edit.Field, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Системный Id " + edit.Field + " задаётся отдельной операцией.", nameof(edit));
        var (parameter, type) = Scalar(edit.Field, schema);
        var value = Convert(parameter, type, edit.Data!, schema);
        Set(nodes, Pair(parameter.Id!, type.ToXData(value)));
        return (parameter.Id!, value);
    }

    /// <summary>
    /// Writes the defaults of the template parameters missing in the object (TZXDATA 6.2): the value the VST shows with
    /// <c>○</c>, the template default over the parameter default; <c>Persist</c> results are written by
    /// <see cref="WritePersisted"/>. Stored values, values outside the template and other RegApps are kept.
    /// </summary>
    private void CommitDefaults(List<Node> nodes, ObjectCalculation calculation, IEnumerable<string> missing, SchemaSnapshot schema)
    {
        foreach (var id in missing)
        {
            if (schema.Formulas.Find(id) != null) continue;
            var (parameter, type) = Scalar(id, schema);
            Set(nodes, Pair(parameter.Id!, type.ToXData(Convert(parameter, type, calculation.Value(id)!, schema))));
        }
    }

    /// <summary>
    /// Writes recalculated <c>Persist</c> results (TZXDATA 7.4). A result is written when it is already stored, when the
    /// template of the object contains it, or when its source parameter has just been set; erroneous formulas are skipped.
    /// </summary>
    private static void WritePersisted(List<Node> nodes, IReadOnlyList<CalculatedValue> results, bool sourceSet, string? template,
        SchemaSnapshot schema, string name)
    {
        var templateParameters = template != null && schema.Templates.TryGetValue(template, out var found) ? found.Parameters : null;
        foreach (var result in results)
        {
            if (!result.Formula.Persist || result.IsError) continue;
            var stored = nodes.Any(node => ParameterSchema.SameId(node.Id, result.Id));
            if (!stored && !sourceSet && templateParameters?.Any(item => ParameterSchema.SameId(item.Id, result.Id)) != true) continue;
            if (ParameterTypeRegistry.Utf8Length(result.Text) > ParameterTypeRegistry.MaxStringBytes)
                throw new InvalidOperationException(name + "результат " + result.Id + " (Persist) длиннее " + ParameterTypeRegistry.MaxStringBytes +
                    " байт UTF-8; операция отменена.");
            Set(nodes, Pair(result.Id, new DataValue(1000, result.Text)));
        }
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
        var error = Check(parameter, type, value, schema.Schema!, out var converted);
        if (error != null) throw new InvalidOperationException("Параметр " + parameter.Id + ": " + error + ".");
        return converted;
    }

    /// <summary>
    /// Converts an entered value to the schema type and checks it (number, <c>Min/Max</c>, enum items, length): the same
    /// check as the write, so the VST reports a wrong input before a transaction (TZXDATA 8.3).
    /// </summary>
    internal static string? Check(SchemaParameter parameter, IParameterType type, object value, ParameterSchema schema, out object converted) =>
        type.Convert(value, out converted) ?? type.CheckValue(Json(converted), parameter, schema);

    private static JsonValue Json(object value) => value switch
    {
        string text => new JsonString(text),
        bool flag => JsonBoolean.From(flag),
        int number => new JsonNumber(number),
        double number => new JsonNumber(number),
        _ => JsonNull.Instance
    };

    internal static string? RowKey(IEnumerable<Node> cells, string key)
    {
        var cell = cells.FirstOrDefault(node => ParameterSchema.SameId(node.Id, key));
        return cell != null && cell.Value.Count == 1 ? XDataV2.FormatValue(cell.Value[0].Value) : null;
    }

    internal static XDataV2.Tokens.RowItem Row(List<Node> cells)
    {
        var all = new List<DataValue> { new DataValue(1002, "{") };
        foreach (var cell in cells) all.AddRange(cell.All);
        all.Add(new DataValue(1002, "}"));
        return new XDataV2.Tokens.RowItem(all, cells);
    }

    internal static Node Pair(string id, DataValue value) => new Node(id, new List<DataValue> { new DataValue(1000, id), value }, 1);

    /// <summary>System pairs go first, after <c>FMT</c>, as in TZXDATA 6.1.</summary>
    internal static void SetSystem(List<Node> nodes, string id, string value)
    {
        var pair = Pair(id, new DataValue(1000, value));
        if (nodes.Any(node => ParameterSchema.SameId(node.Id, id))) { Set(nodes, pair); return; }
        var at = id == XDataV2.TemplateKey ? nodes.FindIndex(node => ParameterSchema.SameId(node.Id, XDataV2.DeviceIdKey)) + 1 : 0;
        nodes.Insert(at, pair);
    }

    /// <summary>Replaces the first pair with the same Id (keeping its spelling) and drops repeated ones, or appends.</summary>
    internal static void Set(List<Node> nodes, Node pair)
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
