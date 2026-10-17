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
    // Most nodes are values without children: the list is created by the first child (TZXDATA 10.6, fewer allocations).
    private List<XDataNode>? children;
    private IReadOnlyList<XDataNode> view = Array.Empty<XDataNode>();
    public XDataNode(string name, string value = "", int? typeCode = null)
    {
        Name = name; Value = value; TypeCode = typeCode;
    }
    public string Name { get; }
    public string Value { get; }
    public int? TypeCode { get; }
    public string DisplayText => Name + (Value.Length == 0 ? "" : ": " + Value);
    public IReadOnlyList<XDataNode> Children => view;
    internal void Add(XDataNode child)
    {
        if (children is null) view = (children = new List<XDataNode>()).AsReadOnly();
        children.Add(child);
    }
}

public sealed class ParsedEntityData
{
    internal ParsedEntityData(EntityDataSnapshot snapshot)
    {
        Handle = snapshot.Handle; EntityType = snapshot.EntityType;
        Tree = new XDataNode(DisplayName);
        if (snapshot.Warning != null) Warnings.Add(snapshot.Warning);
    }

    private ParsedEntityData(ParsedEntityData source)
    {
        Handle = source.Handle; EntityType = source.EntityType; Tree = source.Tree;
        Properties = source.Properties; Materials = source.Materials; Warnings = new List<string>(source.Warnings);
    }

    /// <summary>The same tree, fields and materials with a separate list of warnings (the value cache, TZXDATA 10.2).</summary>
    internal ParsedEntityData Copy() => new ParsedEntityData(this);
    public string Handle { get; }
    public string EntityType { get; }
    public string DisplayName => EntityType + " [" + Handle + "]";
    public XDataNode Tree { get; }
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IList<MaterialItemViewModel> Materials { get; } = new List<MaterialItemViewModel>();
    public IList<string> Warnings { get; } = new List<string>();
}
