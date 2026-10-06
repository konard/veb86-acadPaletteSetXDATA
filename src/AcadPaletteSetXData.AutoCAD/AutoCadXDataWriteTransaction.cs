using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>One native action and transaction for a confirmed palette edit, including its entire selection.</summary>
internal sealed class AutoCadXDataWriteTransaction : IXDataWriteTransaction
{
    private readonly Database database;
    private readonly Dictionary<string, (Entity Entity, EntityDataSnapshot Before)> entities =
        new Dictionary<string, (Entity, EntityDataSnapshot)>(StringComparer.Ordinal);
    private readonly Dictionary<DataRecord, ObjectId> recordIds = new Dictionary<DataRecord, ObjectId>();
    private DocumentLock? documentLock;
    private Transaction? transaction;

    public AutoCadXDataWriteTransaction(Document document, IReadOnlyList<string> handles)
    {
        database = document.Database;
        var snapshots = new List<EntityDataSnapshot>();
        Snapshots = snapshots;
        try
        {
            // Autodesk defines each named lock as an action; localCommandName labels native Undo.
            documentLock = document.LockDocument(DocumentLockMode.Write, "XDATAPALETTEEDIT", "Изменение XDATA", false);
            var selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK) throw new InvalidOperationException("Выделение изменилось.");
            var ids = selection.Value.GetObjectIds();
            if (ids.Length != handles.Count || !new HashSet<string>(ids.Select(id => id.Handle.ToString())).SetEquals(handles))
                throw new InvalidOperationException("Выделение изменилось; повторите ввод после обновления панели.");
            transaction = database.TransactionManager.StartTransaction();
            foreach (var id in ids)
            {
                if (id.IsNull || !id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Entity entity))
                    throw new InvalidOperationException("Выбранный объект больше недоступен.");
                var before = AutoCadDataReader.ReadEntity(transaction, entity, recordIds);
                snapshots.Add(before);
                entities.Add(before.Handle, (entity, before));
            }
        }
        catch { Dispose(); throw; }
    }

    public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }

    public void Write(EntityDataSnapshot snapshot)
    {
        var change = entities[snapshot.Handle];
        // Open the owning entity for write for XML too: locked layers must reject the whole group.
        transaction!.GetObject(change.Entity.ObjectId, OpenMode.ForWrite);
        if (!change.Before.XData.SequenceEqual(snapshot.XData))
        {
            EnsureRegApps(snapshot.XData);
            using var buffer = Buffer(snapshot.XData);
            change.Entity.XData = buffer;
        }
        for (var index = 0; index < change.Before.Records.Count; index++)
        {
            var before = change.Before.Records[index];
            var after = snapshot.Records[index];
            if (ReferenceEquals(before, after)) continue;
            // Use the captured ObjectId rather than parsing the display path of a dictionary key.
            var record = (Xrecord)transaction.GetObject(recordIds[before], OpenMode.ForWrite);
            using var buffer = Buffer(after.Values);
            record.Data = buffer;
        }
    }

    private void EnsureRegApps(IReadOnlyList<DataValue> values)
    {
        var table = (RegAppTable)transaction!.GetObject(database.RegAppTableId, OpenMode.ForRead);
        foreach (var name in values.Where(v => v.TypeCode == 1001).Select(v => (string)v.Value).Distinct())
        {
            if (table.Has(name)) continue;
            if (!table.IsWriteEnabled) transaction.GetObject(database.RegAppTableId, OpenMode.ForWrite);
            using var record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }

    private static ResultBuffer Buffer(IReadOnlyList<DataValue> values) =>
        new ResultBuffer(values.Select(v => new TypedValue(v.TypeCode, v.Value)).ToArray());

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
}
