using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>Coalesces PICKFIRST events and reads detached snapshots on AutoCAD's idle thread.</summary>
internal sealed class AutoCadSelectionSource : ISelectionSource, ISelectionWriter, IDisposable
{
    private readonly DocumentCollection documents = AcApplication.DocumentManager;
    private readonly bool traceEnabled = Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1";
    private Document? document;
    private Document? lastReadDocument;
    private bool pending = true;
    private bool disposed;

    public AutoCadSelectionSource()
    {
        documents.DocumentActivated += OnDocumentActivated;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        AcApplication.Idle += OnIdle;
        Bind(documents.MdiActiveDocument);
    }

    public event EventHandler? SelectionChanged;

    private void Bind(Document? next)
    {
        if (ReferenceEquals(document, next)) return;
        if (document != null)
        {
            document.ImpliedSelectionChanged -= OnSelectionChanged;
            document.CommandEnded -= OnCommandFinished;
            document.CommandCancelled -= OnCommandFinished;
            document.CommandFailed -= OnCommandFinished;
        }
        document = next;
        if (document != null)
        {
            document.ImpliedSelectionChanged += OnSelectionChanged;
            document.CommandEnded += OnCommandFinished;
            document.CommandCancelled += OnCommandFinished;
            document.CommandFailed += OnCommandFinished;
        }
        pending = true;
    }

    private void OnDocumentActivated(object sender, DocumentCollectionEventArgs args) => Bind(args.Document);
    private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
    {
        if (!ReferenceEquals(document, args.Document)) return;
        Bind(null);
        // Clearing is safe here: ReadSelection sees no document and opens no transaction.
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnSelectionChanged(object? sender, EventArgs args) => pending = true;
    private void OnCommandFinished(object sender, CommandEventArgs args) => pending = true;

    private void OnIdle(object? sender, EventArgs args)
    {
        if (disposed) return;
        Bind(documents.MdiActiveDocument);
        if (!pending || (document != null && !document.Editor.IsQuiescent)) return;
        pending = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<EntityDataSnapshot> ReadSelection()
    {
        if (disposed || document == null) return Array.Empty<EntityDataSnapshot>();
        var active = document;
        lastReadDocument = active;
        var timer = Stopwatch.StartNew();
        var snapshots = new List<EntityDataSnapshot>();
        using (active.LockDocument())
        {
            var selection = active.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK) return snapshots;
            using var transaction = active.Database.TransactionManager.StartOpenCloseTransaction();
            foreach (var id in selection.Value.GetObjectIds())
            {
                if (id.IsNull || !id.IsValid || id.IsErased) continue;
                try
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                    snapshots.Add(ReadEntity(transaction, entity));
                }
                catch (Autodesk.AutoCAD.Runtime.Exception error)
                {
                    snapshots.Add(new EntityDataSnapshot(id.Handle.ToString(), "Объект", Array.Empty<DataValue>(), Array.Empty<DataRecord>(), error.Message));
                }
            }
            // ForRead only. Disposing the transaction closes objects without modifying the DWG.
        }
        if (traceEnabled) active.Editor.WriteMessage("\n[XDATAPALETTE] Прочитано объектов: {0}; {1} мс.\n", snapshots.Count, timer.ElapsedMilliseconds);
        return snapshots;
    }

    private static EntityDataSnapshot ReadEntity(Transaction transaction, Entity entity)
    {
        var warning = new List<string>();
        var records = new List<DataRecord>();
        using var buffer = entity.XData;
        var values = Copy(buffer, warning);
        if (!entity.ExtensionDictionary.IsNull)
            ReadDictionary(transaction, entity.ExtensionDictionary, "", records, warning, 0, new HashSet<ObjectId>());
        return new EntityDataSnapshot(entity.Handle.ToString(), entity.GetType().Name, values, records,
            warning.Count == 0 ? null : string.Join("\n", warning));
    }

    public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
    {
        var active = document;
        if (disposed || active == null || !ReferenceEquals(active, lastReadDocument) ||
            !ReferenceEquals(active, documents.MdiActiveDocument) || !active.Editor.IsQuiescent)
            throw new InvalidOperationException("Документ изменился или выполняется команда; повторите ввод после обновления панели.");
        var timer = Stopwatch.StartNew();
        // A named document lock is a native AutoCAD action and provides an Undo boundary.
        using (active.LockDocument(DocumentLockMode.Write, "XDATAPALETTEEDIT", "Изменение XDATA", false))
        {
            var selection = active.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK) throw new InvalidOperationException("Выделение изменилось.");
            var ids = selection.Value.GetObjectIds();
            if (ids.Length != handles.Count || !new HashSet<string>(ids.Select(id => id.Handle.ToString())).SetEquals(handles))
                throw new InvalidOperationException("Выделение изменилось; повторите ввод после обновления панели.");
            using var transaction = active.Database.TransactionManager.StartTransaction();
            var changes = new List<(Entity Entity, EntityDataSnapshot Before, EntityDataSnapshot After)>();
            foreach (var id in ids)
            {
                if (id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Entity entity))
                    throw new InvalidOperationException("Выбранный объект больше недоступен.");
                var before = ReadEntity(transaction, entity);
                var after = new XDataPatch().Apply(before, edit);
                foreach (var value in after.XData.Where(v => v.TypeCode == 1000))
                    if (Encoding.UTF8.GetByteCount((string)value.Value) > 255)
                        throw new InvalidOperationException("Строка XDATA превышает 255 байт; используйте более короткое значение.");
                changes.Add((entity, before, after));
            }
            // Preflight the whole group before opening anything for write. Any later exception rolls it all back.
            foreach (var change in changes)
            {
                if (!change.Before.XData.SequenceEqual(change.After.XData))
                {
                    EnsureRegApps(transaction, active.Database, change.After.XData);
                    change.Entity.UpgradeOpen();
                    using var buffer = Buffer(change.After.XData);
                    change.Entity.XData = buffer;
                }
                for (var index = 0; index < change.Before.Records.Count; index++)
                {
                    if (ReferenceEquals(change.Before.Records[index], change.After.Records[index])) continue;
                    var id = change.Entity.ExtensionDictionary;
                    var path = change.After.Records[index].Name.Split('/');
                    foreach (var key in path)
                        id = ((DBDictionary)transaction.GetObject(id, OpenMode.ForRead)).GetAt(key);
                    var record = (Xrecord)transaction.GetObject(id, OpenMode.ForWrite);
                    using var buffer = Buffer(change.After.Records[index].Values);
                    record.Data = buffer;
                }
            }
            transaction.Commit();
        }
        pending = true;
        if (traceEnabled) active.Editor.WriteMessage("\n[XDATAPALETTE] Записано объектов: {0}; операция {1}/{2}; {3} мс.\n", handles.Count, edit.Kind, edit.Field, timer.ElapsedMilliseconds);
    }

    private static ResultBuffer Buffer(IReadOnlyList<DataValue> values) =>
        new ResultBuffer(values.Select(value => new TypedValue(value.TypeCode, value.Value)).ToArray());

    private static void EnsureRegApps(Transaction transaction, Database database, IReadOnlyList<DataValue> values)
    {
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        foreach (var name in values.Where(value => value.TypeCode == 1001).Select(value => (string)value.Value))
        {
            if (table.Has(name)) continue;
            if (!table.IsWriteEnabled) table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = name };
            table.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
        }
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
        IList<string> warnings, int depth, ISet<ObjectId> visited)
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
                    records.Add(new DataRecord(path, Copy(data, warnings)));
                }
                else if (item is DBDictionary) ReadDictionary(transaction, entry.Value, path, records, warnings, depth + 1, visited);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception error) { warnings.Add(path + ": " + error.Message); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        AcApplication.Idle -= OnIdle;
        documents.DocumentActivated -= OnDocumentActivated;
        documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        Bind(null);
    }
}
