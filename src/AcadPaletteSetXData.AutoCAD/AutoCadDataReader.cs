using System;
using System.Collections.Generic;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>Copies complete, detached buffers and optionally tracks native XRecord identities for writing.</summary>
internal static class AutoCadDataReader
{
    public static EntityDataSnapshot ReadEntity(Transaction transaction, Entity entity, IDictionary<DataRecord, ObjectId>? recordIds = null)
    {
        var warning = new List<string>();
        var records = new List<DataRecord>();
        using var buffer = entity.XData;
        var values = Copy(buffer, warning);
        if (!entity.ExtensionDictionary.IsNull)
            ReadDictionary(transaction, entity.ExtensionDictionary, "", records, warning, 0, new HashSet<ObjectId>(), recordIds);
        return new EntityDataSnapshot(entity.Handle.ToString(), entity.GetType().Name, values, records,
            warning.Count == 0 ? null : string.Join("\n", warning));
    }

    private static IReadOnlyList<DataValue> Copy(ResultBuffer? buffer, IList<string> warnings)
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

    private static void ReadDictionary(Transaction transaction, ObjectId id, string prefix, IList<DataRecord> records,
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
