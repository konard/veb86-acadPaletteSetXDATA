using System;
using System.Collections.Generic;
using System.Diagnostics;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>Coalesces PICKFIRST events and reads detached snapshots on AutoCAD's idle thread.</summary>
internal sealed class AutoCadSelectionSource : ISelectionSource, IDisposable
{
    private readonly DocumentCollection documents = AcApplication.DocumentManager;
    private readonly bool traceEnabled = Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1";
    private Document? document;
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
                    var warning = new List<string>();
                    var records = new List<DataRecord>();
                    using var buffer = entity.XData;
                    var values = Copy(buffer, warning);
                    if (!entity.ExtensionDictionary.IsNull)
                        ReadDictionary(transaction, entity.ExtensionDictionary, "", records, warning, 0, new HashSet<ObjectId>());
                    snapshots.Add(new EntityDataSnapshot(entity.Handle.ToString(), entity.GetType().Name, values, records,
                        warning.Count == 0 ? null : string.Join("\n", warning)));
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
