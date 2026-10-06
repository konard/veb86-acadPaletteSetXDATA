using System;
using System.Collections.Generic;

namespace AcadPaletteSetXData.Core;

public interface ISelectionWriter
{
    /// <summary>Apply one field operation atomically to the specified current selection.</summary>
    void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit);
}

public enum SelectionEditKind { Header, MaterialField, AddMaterial, DeleteMaterial }

/// <summary>A confirmed change, never a serialization of the merged UI.</summary>
public sealed class SelectionEdit : EventArgs
{
    private SelectionEdit(SelectionEditKind kind, string field = "", string value = "", string materialKey = "", MaterialItemViewModel? material = null)
    { Kind = kind; Field = field; Value = value; MaterialKey = materialKey; Material = material; }
    public SelectionEditKind Kind { get; }
    public string Field { get; }
    public string Value { get; }
    public string MaterialKey { get; }
    public MaterialItemViewModel? Material { get; }
    public static SelectionEdit Header(string field, string value) => new SelectionEdit(SelectionEditKind.Header, field, value);
    public static SelectionEdit MaterialField(string key, string field, string value) => new SelectionEdit(SelectionEditKind.MaterialField, field, value, key);
    public static SelectionEdit Add(MaterialItemViewModel material) => new SelectionEdit(SelectionEditKind.AddMaterial, material: material);
    public static SelectionEdit Delete(string key) => new SelectionEdit(SelectionEditKind.DeleteMaterial, materialKey: key);
}
