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

    public AutoCadXDataWriteTransaction(Document document, IReadOnlyList<string> handles) : this(document, handles, null) { }

    /// <param name="selected">Objects chosen by a command (<c>VELEC_TEMPLATE_APPLY</c>); null — the PICKFIRST selection of the palette.</param>
    /// <param name="command">The global and the local name of the native Undo action.</param>
    public AutoCadXDataWriteTransaction(Document document, IReadOnlyList<string> handles, IReadOnlyList<ObjectId>? selected,
        (string Global, string Local)? command = null)
    {
        database = document.Database;
        var snapshots = new List<EntityDataSnapshot>();
        Snapshots = snapshots;
        try
        {
            // Autodesk defines each named lock as an action; localCommandName labels native Undo.
            var name = command ?? ("XDATAPALETTEEDIT", "Изменение XDATA");
            documentLock = document.LockDocument(DocumentLockMode.Write, name.Global, name.Local, false);
            var ids = selected?.ToArray() ?? Implied(document);
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

    private static ObjectId[] Implied(Document document)
    {
        var selection = document.Editor.SelectImplied();
        if (selection.Status != PromptStatus.OK) throw new InvalidOperationException("Выделение изменилось.");
        return selection.Value.GetObjectIds();
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
            using var buffer = Buffer(Removed(change.Before.XData, snapshot.XData));
            change.Entity.XData = buffer;
        }
        if (change.Before.Records.Count == snapshot.Records.Count)
        {
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
            return;
        }
        // VELEC_MIGRATE with «Удалить»: records are only removed, the kept ones are the same instances.
        if (snapshot.Records.Any(after => !change.Before.Records.Any(before => ReferenceEquals(before, after))))
            throw new InvalidOperationException("Объект " + snapshot.Handle + ": XRecord можно только сохранить или удалить.");
        foreach (var before in change.Before.Records.Where(before => !snapshot.Records.Any(after => ReferenceEquals(before, after))))
            transaction.GetObject(recordIds[before], OpenMode.ForWrite).Erase();
    }

    /// <summary>
    /// Setting <c>Entity.XData</c> replaces only the RegApps present in the buffer; an empty <c>(1001 "APP")</c> removes
    /// a RegApp whose data is gone (source data deleted by <c>VELEC_MIGRATE</c>).
    /// </summary>
    private static IReadOnlyList<DataValue> Removed(IReadOnlyList<DataValue> before, IReadOnlyList<DataValue> after)
    {
        var kept = new HashSet<string>(after.Where(v => v.TypeCode == 1001).Select(v => (string)v.Value), StringComparer.OrdinalIgnoreCase);
        var removed = before.Where(v => v.TypeCode == 1001).Select(v => (string)v.Value).Where(name => !kept.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (removed.Length == 0) return after;
        var values = after.ToList();
        values.AddRange(removed.Select(name => new DataValue(1001, name)));
        return values;
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
