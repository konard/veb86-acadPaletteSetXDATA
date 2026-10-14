using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>Detached records of <c>NOD/VELEC</c> and the ObjectIds whose changes affect them.</summary>
internal sealed class AutoCadSchemaNodSnapshot
{
    public AutoCadSchemaNodSnapshot(IReadOnlyList<DataRecord> records, IReadOnlyList<string> warnings,
        IReadOnlyDictionary<string, ObjectId> recordIds, IReadOnlyCollection<ObjectId> trackedIds)
    {
        Records = records; Warnings = warnings; RecordIds = recordIds; TrackedIds = trackedIds;
    }

    public IReadOnlyList<DataRecord> Records { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Xrecord by its path under <c>VELEC</c> (case-insensitive, like dictionary keys).</summary>
    public IReadOnlyDictionary<string, ObjectId> RecordIds { get; }
    /// <summary>NOD, <c>VELEC</c>, its dictionaries and Xrecords.</summary>
    public IReadOnlyCollection<ObjectId> TrackedIds { get; }

    public SchemaNodReadResult ToResult() => SchemaNod.FromRecords(Records, Warnings);
}

internal static class AutoCadSchemaNod
{
    public static AutoCadSchemaNodSnapshot Read(Transaction transaction, Database database)
    {
        var records = new List<DataRecord>();
        var warnings = new List<string>();
        var ids = new Dictionary<DataRecord, ObjectId>();
        var visited = new HashSet<ObjectId>();
        var nod = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(DrawingNames.NodRoot))
        {
            var rootId = nod.GetAt(DrawingNames.NodRoot);
            if (transaction.GetObject(rootId, OpenMode.ForRead) is DBDictionary)
                AutoCadDataReader.ReadDictionary(transaction, rootId, "", records, warnings, 0, visited, ids);
            else
            {
                warnings.Add(DrawingNames.NodRoot + " не является словарём (DBDictionary).");
                visited.Add(rootId);
            }
        }
        var recordIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ids)
            if (!recordIds.ContainsKey(pair.Key.Name)) recordIds.Add(pair.Key.Name, pair.Value);
        var tracked = new HashSet<ObjectId>(visited) { database.NamedObjectsDictionaryId };
        tracked.UnionWith(ids.Values);
        return new AutoCadSchemaNodSnapshot(records, warnings, recordIds, tracked);
    }

    /// <summary>Reads the schema of <paramref name="database"/> without modifying it.</summary>
    public static AutoCadSchemaNodSnapshot Read(Database database)
    {
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var snapshot = Read(transaction, database);
        transaction.Commit();
        return snapshot;
    }
}

/// <summary>
/// One document lock and one transaction over <c>NOD/VELEC</c>: the import is a single native Undo step.
/// Dictionaries <c>VELEC</c> and sections are created on demand.
/// </summary>
internal sealed class AutoCadSchemaNodTransaction : ISchemaNodTransaction
{
    private readonly Database database;
    private readonly Dictionary<string, ObjectId> recordIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
    private DocumentLock? documentLock;
    private Transaction? transaction;

    public AutoCadSchemaNodTransaction(Document document, string commandName, string localName)
    {
        database = document.Database;
        try
        {
            documentLock = document.LockDocument(DocumentLockMode.Write, commandName, localName, false);
            transaction = database.TransactionManager.StartTransaction();
            var snapshot = AutoCadSchemaNod.Read(transaction, database);
            Records = snapshot.Records;
            Warnings = snapshot.Warnings;
            foreach (var pair in snapshot.RecordIds) recordIds.Add(pair.Key, pair.Value);
        }
        catch { Dispose(); throw; }
    }

    public IReadOnlyList<DataRecord> Records { get; } = Array.Empty<DataRecord>();
    public IReadOnlyList<string> Warnings { get; } = Array.Empty<string>();

    public void Write(DataRecord record)
    {
        var (section, key) = Split(record.Name);
        var parent = Dictionary(Dictionary(database.NamedObjectsDictionaryId, DrawingNames.NodRoot), section);
        using var buffer = new ResultBuffer(record.Values.Select(v => new TypedValue(v.TypeCode, v.Value)).ToArray());
        if (recordIds.TryGetValue(record.Name, out var id) && !id.IsErased)
        {
            var existing = (Xrecord)transaction!.GetObject(id, OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }
        var dictionary = (DBDictionary)transaction!.GetObject(parent, OpenMode.ForWrite);
        if (dictionary.Contains(key))
            throw new InvalidOperationException(DrawingNames.NodRoot + "/" + record.Name + " занят объектом другого типа.");
        var created = new Xrecord { Data = buffer };
        recordIds[record.Name] = dictionary.SetAt(key, created);
        transaction.AddNewlyCreatedDBObject(created, true);
    }

    public void Erase(string path)
    {
        if (!recordIds.TryGetValue(path, out var id)) return;
        var (section, key) = Split(path);
        var parent = Dictionary(Dictionary(database.NamedObjectsDictionaryId, DrawingNames.NodRoot), section);
        // Remove the key first so that a record with the same Id in another case can take it.
        var dictionary = (DBDictionary)transaction!.GetObject(parent, OpenMode.ForWrite);
        if (dictionary.Contains(key) && dictionary.GetAt(key) == id) dictionary.Remove(key);
        if (!id.IsErased) transaction.GetObject(id, OpenMode.ForWrite).Erase();
        recordIds.Remove(path);
    }

    public void Commit() => transaction!.Commit();

    public void Dispose()
    {
        try { transaction?.Dispose(); }
        finally
        {
            transaction = null;
            documentLock?.Dispose();
            documentLock = null;
        }
    }

    /// <summary><c>INFO</c> lives in <c>VELEC</c> itself, objects in section dictionaries.</summary>
    private static (string? Section, string Key) Split(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? (null, path) : (path.Substring(0, slash), path.Substring(slash + 1));
    }

    private ObjectId Dictionary(ObjectId parentId, string? key)
    {
        if (key is null) return parentId;
        var parent = (DBDictionary)transaction!.GetObject(parentId, OpenMode.ForRead);
        if (parent.Contains(key))
        {
            var id = parent.GetAt(key);
            if (transaction.GetObject(id, OpenMode.ForRead) is DBDictionary) return id;
            throw new InvalidOperationException(key + " в " + DrawingNames.NodRoot + " не является словарём (DBDictionary).");
        }
        // GetObject instead of UpgradeOpen: the parent may already be open for write in this transaction
        // (VELEC just created, or a second section created), and UpgradeOpen would throw eWasOpenForWrite.
        var writable = (DBDictionary)transaction.GetObject(parentId, OpenMode.ForWrite);
        var created = new DBDictionary();
        var createdId = writable.SetAt(key, created);
        transaction.AddNewlyCreatedDBObject(created, true);
        return createdId;
    }
}
