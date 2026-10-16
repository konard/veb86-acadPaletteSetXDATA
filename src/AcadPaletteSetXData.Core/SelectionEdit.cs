using System;
using System.Collections.Generic;

namespace AcadPaletteSetXData.Core;

public interface ISelectionWriter
{
    /// <summary>Apply one field operation atomically to the specified current selection.</summary>
    void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit);
}

public enum SelectionEditKind
{
    // XDATA v1 and XML VisualTreeString (stage 4 of the first specification).
    Header, MaterialField, AddMaterial, DeleteMaterial,
    // XDATA v2 (TZXDATA 6): one parameter, or one row of a Table.
    Parameter, RemoveParameter, TableCell, AddTableRow, DeleteTableRow, Template, NewDeviceId,
    // TZXDATA 7.4: rewrite the Persist results of CalculatedString (the recalculation of VELEC_CHECK).
    RecalculatePersisted,
    // TZXDATA 6.2: «Зафиксировать значения по умолчанию» of the template.
    CommitDefaults
}

/// <summary>A confirmed change, never a serialization of the merged UI.</summary>
public sealed class SelectionEdit : EventArgs
{
    private SelectionEdit(SelectionEditKind kind, string field = "", string value = "", string materialKey = "", MaterialItemViewModel? material = null)
    { Kind = kind; Field = field; Value = value; MaterialKey = materialKey; Material = material; }
    public SelectionEditKind Kind { get; }
    /// <summary>v1: the field; v2: the parameter, the table column of <see cref="SelectionEditKind.TableCell"/> or the template.</summary>
    public string Field { get; }
    public string Value { get; }
    public string MaterialKey { get; }
    public MaterialItemViewModel? Material { get; }
    /// <summary>v2: the edited value, typed (<c>string</c>, <c>double</c>, <c>int</c>, <c>bool</c>) or text converted to the schema type.</summary>
    public object? Data { get; private set; }
    /// <summary>v2: the <c>Table</c> parameter of a row operation.</summary>
    public string Table { get; private set; } = "";
    /// <summary>v2: the key column text of the row (TZXDATA 4.2).</summary>
    public string RowKey { get; private set; } = "";
    /// <summary>v2: the cells of an added row.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> Cells { get; private set; } = Array.Empty<KeyValuePair<string, object>>();
    public bool IsV2 => Kind >= SelectionEditKind.Parameter;
    public static SelectionEdit Header(string field, string value) => new SelectionEdit(SelectionEditKind.Header, field, value);
    public static SelectionEdit MaterialField(string key, string field, string value) => new SelectionEdit(SelectionEditKind.MaterialField, field, value, key);
    public static SelectionEdit Add(MaterialItemViewModel material) => new SelectionEdit(SelectionEditKind.AddMaterial, material: material);
    public static SelectionEdit Delete(string key) => new SelectionEdit(SelectionEditKind.DeleteMaterial, materialKey: key);

    /// <summary>Sets one v2 parameter in every object; other values are kept.</summary>
    public static SelectionEdit SetParameter(string id, object value) =>
        new SelectionEdit(SelectionEditKind.Parameter, id, Text(value)) { Data = value ?? throw new ArgumentNullException(nameof(value)) };
    /// <summary>Removes one v2 value, including values outside the schema (TZXDATA 6.5).</summary>
    public static SelectionEdit RemoveParameter(string id) => new SelectionEdit(SelectionEditKind.RemoveParameter, id);
    /// <summary>Sets a cell of the row with <paramref name="rowKey"/>; a missing row is added, as in group editing of materials.</summary>
    public static SelectionEdit SetTableCell(string table, string rowKey, string column, object value) =>
        new SelectionEdit(SelectionEditKind.TableCell, column, Text(value)) { Table = table, RowKey = rowKey, Data = value ?? throw new ArgumentNullException(nameof(value)) };
    /// <summary>Adds a row; the cells must contain the key column.</summary>
    public static SelectionEdit AddTableRow(string table, IReadOnlyList<KeyValuePair<string, object>> cells) =>
        new SelectionEdit(SelectionEditKind.AddTableRow) { Table = table, Cells = cells ?? throw new ArgumentNullException(nameof(cells)) };
    public static SelectionEdit DeleteTableRow(string table, string rowKey) =>
        new SelectionEdit(SelectionEditKind.DeleteTableRow) { Table = table, RowKey = rowKey };
    /// <summary>Sets <c>TEMPLATE</c> (an empty id removes it) and creates a missing <c>DEVICE_ID</c> (TZXDATA 6.4, 6.6).</summary>
    public static SelectionEdit SetTemplate(string template) => new SelectionEdit(SelectionEditKind.Template, template ?? "", template ?? "");
    /// <summary>Gives every object its own new <c>DEVICE_ID</c>.</summary>
    public static SelectionEdit NewDeviceId() => new SelectionEdit(SelectionEditKind.NewDeviceId, XDataV2.DeviceIdKey);
    /// <summary>
    /// Writes the current results of <c>Persist</c> formulas that are stored or belong to the template of an object
    /// (TZXDATA 7.4); objects without XDATA v2 are left as they are.
    /// </summary>
    public static SelectionEdit RecalculatePersisted() => new SelectionEdit(SelectionEditKind.RecalculatePersisted);
    /// <summary>
    /// «Зафиксировать значения по умолчанию» (TZXDATA 6.2): writes the shown defaults of the template parameters that are
    /// not stored, and the <c>Persist</c> results of the template; stored values are kept. Objects without a template
    /// have no defaults and are left as they are.
    /// </summary>
    public static SelectionEdit CommitDefaults() => new SelectionEdit(SelectionEditKind.CommitDefaults);

    private static string Text(object? value) => value is null ? "" : XDataV2.FormatValue(value);
}
