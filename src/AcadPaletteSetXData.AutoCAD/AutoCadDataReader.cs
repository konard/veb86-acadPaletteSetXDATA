using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.DatabaseServices;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>Copies complete, detached buffers and optionally tracks native XRecord identities for writing.</summary>
internal sealed class AutoCadDataReader
{
    private static readonly ConcurrentDictionary<Type, (PropertyInfo? Length, PropertyInfo? Area)> Measurements =
        new ConcurrentDictionary<Type, (PropertyInfo?, PropertyInfo?)>();
    private readonly Transaction transaction;
    private readonly Dictionary<ObjectId, IReadOnlyList<KeyValuePair<string, string>>> constants =
        new Dictionary<ObjectId, IReadOnlyList<KeyValuePair<string, string>>>();

    public AutoCadDataReader(Transaction transaction) => this.transaction = transaction;

    /// <param name="typeDisplayName">The name AutoCAD shows for the object type (issue #41); <c>GetType().Name</c> when null.</param>
    public EntityDataSnapshot ReadEntity(Entity entity, IDictionary<DataRecord, ObjectId>? recordIds = null,
        string? typeDisplayName = null)
    {
        var warning = new List<string>();
        var records = new List<DataRecord>();
        using var buffer = entity.XData;
        var values = Copy(buffer, warning);
        if (!entity.ExtensionDictionary.IsNull)
            ReadDictionary(transaction, entity.ExtensionDictionary, "", records, warning, 0, new HashSet<ObjectId>(), recordIds);
        var properties = ReadProperties(entity, warning);
        return new EntityDataSnapshot(entity.Handle.ToString(), entity.GetType().Name, values, records,
            warning.Count == 0 ? null : string.Join("\n", warning), typeDisplayName, properties);
    }

    private EntityProperties ReadProperties(Entity entity, IList<string> warnings)
    {
        List<KeyValuePair<string, string>>? attributes = null;
        if (entity is BlockReference block)
        {
            attributes = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (ObjectId id in block.AttributeCollection)
                {
                    if (!Readable(id)) continue;
                    try
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead) is AttributeReference attribute)
                        {
                            using var text = attribute.IsMTextAttribute ? attribute.MTextAttribute : null;
                            attributes.Add(new KeyValuePair<string, string>(attribute.Tag, text?.Text ?? attribute.TextString));
                        }
                    }
                    catch (AcException error) { warnings.Add("Атрибут [" + id.Handle + "]: " + error.Message); }
                }
                attributes.AddRange(ConstantAttributes(block.BlockTableRecord, warnings));
            }
            catch (AcException error) { warnings.Add("Атрибуты: " + error.Message); }
        }

        // Some entities expose measurements independently of Curve (Hatch, Region and custom entities).
        var (lengthProperty, areaProperty) = Measurements.GetOrAdd(entity.GetType(), type =>
            (MeasurementProperty(type, "Length") ?? MeasurementProperty(type, "Perimeter"), MeasurementProperty(type, "Area")));
        double? length = null;
        double? area = null;
        if (!(entity is Ray || entity is Xline))
        {
            length = Measure(entity, lengthProperty);
            if (length == null && entity is Curve curve)
            {
                // The distance at EndParam is measured along the curve from its start.
                try { length = curve.GetDistanceAtParameter(curve.EndParam); }
                catch (AcException) { }
            }
            if (!(entity is Line)) area = Measure(entity, areaProperty);
        }
        return (attributes == null || attributes.Count == 0) && length == null && area == null
            ? EntityProperties.Empty : new EntityProperties(attributes, length, area);
    }

    private IReadOnlyList<KeyValuePair<string, string>> ConstantAttributes(ObjectId definitionId, IList<string> warnings)
    {
        if (!Readable(definitionId)) return Array.Empty<KeyValuePair<string, string>>();
        if (constants.TryGetValue(definitionId, out var cached)) return cached;

        var result = new List<KeyValuePair<string, string>>();
        if (transaction.GetObject(definitionId, OpenMode.ForRead) is BlockTableRecord definition && definition.HasAttributeDefinitions)
            foreach (ObjectId id in definition)
            {
                if (!Readable(id) || id.ObjectClass.DxfName != "ATTDEF") continue;
                try
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attribute && attribute.Constant)
                    {
                        using var text = attribute.IsMTextAttributeDefinition ? attribute.MTextAttributeDefinition : null;
                        result.Add(new KeyValuePair<string, string>(attribute.Tag, text?.Text ?? attribute.TextString));
                    }
                }
                catch (AcException error) { warnings.Add("Атрибут [" + id.Handle + "]: " + error.Message); }
            }
        return constants[definitionId] = result;
    }

    private static bool Readable(ObjectId id) => !id.IsNull && id.IsValid && !id.IsErased;

    private static PropertyInfo? MeasurementProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public, null, typeof(double), Type.EmptyTypes, null);

    private static double? Measure(Entity entity, PropertyInfo? property)
    {
        try { return property?.GetValue(entity) as double?; }
        catch (TargetInvocationException error) when (error.InnerException is AcException)
        {
            // A property such as Curve.Area can exist on a type without applying to this instance.
            return null;
        }
    }

    internal static IReadOnlyList<DataValue> Copy(ResultBuffer? buffer, IList<string> warnings)
    {
        var values = new List<DataValue>();
        if (buffer == null) return values;
        foreach (TypedValue value in buffer)
        {
            if (values.Count == XDataParser.MaxValues) { warnings.Add("Превышен лимит ResultBuffer."); break; }
            values.Add(new DataValue(value.TypeCode, value.Value is byte[] bytes ? bytes.Clone() : value.Value));
        }
        return values;
    }

    internal static void ReadDictionary(Transaction transaction, ObjectId id, string prefix, IList<DataRecord> records,
        IList<string> warnings, int depth, ISet<ObjectId> visited, IDictionary<DataRecord, ObjectId>? recordIds)
    {
        if (depth > 32 || visited.Count >= 4096 || !visited.Add(id))
        {
            warnings.Add("Превышены пределы ExtensionDictionary или обнаружен цикл."); return;
        }
        if (!(transaction.GetObject(id, OpenMode.ForRead) is DBDictionary dictionary)) return;
        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (entry.Value.IsNull || entry.Value.IsErased) continue;
            if (records.Count >= 4096) { warnings.Add("Превышен лимит XRecord."); break; }
            var path = prefix.Length == 0 ? entry.Key : prefix + "/" + entry.Key;
            try
            {
                var item = transaction.GetObject(entry.Value, OpenMode.ForRead);
                if (item is Xrecord record)
                {
                    using var data = record.Data;
                    var snapshot = new DataRecord(path, Copy(data, warnings));
                    records.Add(snapshot);
                    recordIds?.Add(snapshot, entry.Value);
                }
                else if (item is DBDictionary) ReadDictionary(transaction, entry.Value, path, records, warnings, depth + 1, visited, recordIds);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception error) { warnings.Add(path + ": " + error.Message); }
        }
    }
}
