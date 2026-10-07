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
                    snapshots.Add(AutoCadDataReader.ReadEntity(transaction, entity));
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

    public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
    {
        var active = document;
        if (disposed || active == null || !ReferenceEquals(active, lastReadDocument) ||
            !ReferenceEquals(active, documents.MdiActiveDocument) || !active.Editor.IsQuiescent)
            throw new InvalidOperationException("Документ изменился или выполняется команда; повторите ввод после обновления панели.");
        var timer = Stopwatch.StartNew();
        try
        {
            new XDataWriter(targets => new AutoCadXDataWriteTransaction(active, targets)).WriteSelection(handles, edit);
        }
        finally { pending = true; }
        if (traceEnabled) active.Editor.WriteMessage("\n[XDATAPALETTE] Записано объектов: {0}; операция {1}/{2}; {3} мс.\n", handles.Count, edit.Kind, edit.Field, timer.ElapsedMilliseconds);
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
