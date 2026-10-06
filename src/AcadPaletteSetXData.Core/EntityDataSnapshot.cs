using System;
using System.Collections.Generic;

namespace AcadPaletteSetXData.Core;

/// <summary>Detached values: no AutoCAD objects or disposable buffers cross into Core.</summary>
public sealed class DataValue
{
    public DataValue(int typeCode, object value) { TypeCode = typeCode; Value = value; }
    public int TypeCode { get; }
    public object Value { get; }
}

public sealed class DataRecord
{
    public DataRecord(string name, IReadOnlyList<DataValue> values) { Name = name; Values = values; }
    public string Name { get; }
    public IReadOnlyList<DataValue> Values { get; }
}

public sealed class EntityDataSnapshot
{
    public EntityDataSnapshot(string handle, string entityType, IReadOnlyList<DataValue> xData,
        IReadOnlyList<DataRecord> records, string? warning = null)
    {
        Handle = handle; EntityType = entityType; XData = xData; Records = records; Warning = warning;
    }
    public string Handle { get; }
    public string EntityType { get; }
    public IReadOnlyList<DataValue> XData { get; }
    public IReadOnlyList<DataRecord> Records { get; }
    public string? Warning { get; }
}

public sealed class XDataNode
{
    private readonly List<XDataNode> children = new List<XDataNode>();
    public XDataNode(string name, string value = "", int? typeCode = null)
    {
        Name = name; Value = value; TypeCode = typeCode; Children = children.AsReadOnly();
    }
    public string Name { get; }
    public string Value { get; }
    public int? TypeCode { get; }
    public string DisplayText => Name + (Value.Length == 0 ? "" : ": " + Value);
    public IReadOnlyList<XDataNode> Children { get; }
    internal void Add(XDataNode child) => children.Add(child);
}

public sealed class ParsedEntityData
{
    internal ParsedEntityData(EntityDataSnapshot snapshot)
    {
        Handle = snapshot.Handle; EntityType = snapshot.EntityType;
        Tree = new XDataNode(DisplayName);
        if (snapshot.Warning != null) Warnings.Add(snapshot.Warning);
    }
    public string Handle { get; }
    public string EntityType { get; }
    public string DisplayName => EntityType + " [" + Handle + "]";
    public XDataNode Tree { get; }
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IList<MaterialItemViewModel> Materials { get; } = new List<MaterialItemViewModel>();
    public IList<string> Warnings { get; } = new List<string>();
}
