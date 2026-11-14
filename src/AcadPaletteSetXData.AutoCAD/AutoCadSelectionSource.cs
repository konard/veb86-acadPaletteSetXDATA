using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Internal.PropertyInspector;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>Coalesces PICKFIRST events and reads detached snapshots on AutoCAD's idle thread.</summary>
internal sealed class AutoCadSelectionSource : ISelectionSource, ISelectionWriter, ISelectionChanger, IObjectValuesSource, IDisposable
{
    private readonly DocumentCollection documents = AcApplication.DocumentManager;
    private const int TracedCalculations = 10;
    private readonly bool traceEnabled = Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1";
    private Document? document;
    private Document? lastReadDocument;
    private bool pending = true;
    private bool disposed;
    private bool displayNameFailed;

    public AutoCadSelectionSource()
    {
        documents.DocumentActivated += OnDocumentActivated;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        AcApplication.Idle += OnIdle;
        Bind(documents.MdiActiveDocument);
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Value cache of the document read last; XDATA v2 of unchanged objects is not parsed again.</summary>
    public ObjectValuesCache? Values => disposed || lastReadDocument == null ? null : AutoCadSchemaCaches.ValuesFor(lastReadDocument);

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
                    snapshots.Add(AutoCadDataReader.ReadEntity(transaction, entity, typeDisplayName: TypeDisplayName(entity)));
                }
                catch (Autodesk.AutoCAD.Runtime.Exception error)
                {
                    snapshots.Add(new EntityDataSnapshot(id.Handle.ToString(), "Объект", Array.Empty<DataValue>(), Array.Empty<DataRecord>(), error.Message));
                }
            }
            // ForRead only. Disposing the transaction closes objects without modifying the DWG.
            // The schema is loaded and XDATA v2 is parsed into the value cache under the same lock;
            // the selection model then reuses these entries without parsing again.
            AutoCadSchemaCaches.ValuesFor(active).Get(snapshots);
        }
        if (traceEnabled)
        {
            var values = AutoCadSchemaCaches.ValuesFor(active);
            active.Editor.WriteMessage("\n[XDATAPALETTE] Прочитано объектов: {0}; {1} мс; кэш значений: {2}, разборов XDATA: {3}.\n",
                snapshots.Count, timer.ElapsedMilliseconds, values.Count, values.ParseCount);
            // CalculatedString results (TZXDATA 7.3) come from the cached values: the parse counter stays the same.
            foreach (var item in values.Get(snapshots.Take(TracedCalculations)))
                active.Editor.WriteMessage("[XDATAPALETTE] {0}\n", new ObjectCalculation(item).Describe());
        }
        return snapshots;
    }

    /// <summary>
    /// The name of the object type in the language of the installed AutoCAD: the name the Properties palette shows for the
    /// object («Отрезок» in the Russian AutoCAD, «Line» in the English one, issue #41). AutoCAD gives it through the
    /// Object Property Manager of the object; the plugin has no list of names. Null when AutoCAD gives no name: the snapshot
    /// then falls back to the .NET type.
    /// </summary>
    private string? TypeDisplayName(Entity entity)
    {
        try
        {
            var name = ObjectPropertyManagerProperties.GetDisplayName(entity);
            if (string.IsNullOrWhiteSpace(name) && traceEnabled)
                document?.Editor.WriteMessage("\n[XDATAPALETTE] AutoCAD не вернул имя типа {0} [{1}].\n", entity.GetType().Name, entity.Handle);
            return name;
        }
        catch (System.Exception error)
        {
            // Reported once per session in the trace: the list falls back to the .NET type names.
            if (!displayNameFailed) System.Diagnostics.Trace.WriteLine(error, "XDATAPALETTE type display name");
            if (!displayNameFailed && traceEnabled)
                document?.Editor.WriteMessage("\n[XDATAPALETTE] Имя типа не получено от AutoCAD: {0}\n", error.Message);
            displayNameFailed = true;
            return null;
        }
    }

    /// <summary>
    /// * and − of the group list (issue #41): selects the objects of the current PICKFIRST set with these handles through
    /// <c>Editor.SetImpliedSelection</c>; an empty list clears the selection. Objects are not opened for
    /// write, XDATA is not changed.
    /// </summary>
    public void SetSelection(IReadOnlyList<string> handles)
    {
        var active = Ready();
        try
        {
            using (active.LockDocument())
            {
                var keep = new HashSet<string>(handles, StringComparer.OrdinalIgnoreCase);
                var selection = active.Editor.SelectImplied();
                var ids = selection.Status == PromptStatus.OK
                    ? selection.Value.GetObjectIds().Where(id => !id.IsNull && keep.Contains(id.Handle.ToString())).ToArray()
                    : Array.Empty<ObjectId>();
                active.Editor.SetImpliedSelection(ids);
                if (traceEnabled) active.Editor.WriteMessage("\n[XDATAPALETTE] Выделено объектов: {0} из {1}.\n", ids.Length, handles.Count);
            }
        }
        finally { pending = true; }
    }

    /// <summary>The document read last is active and idle: the palette may write or change its selection.</summary>
    private Document Ready()
    {
        var active = document;
        if (disposed || active == null || !ReferenceEquals(active, lastReadDocument) ||
            !ReferenceEquals(active, documents.MdiActiveDocument) || !active.Editor.IsQuiescent)
            throw new InvalidOperationException("Документ изменился или выполняется команда; повторите ввод после обновления панели.");
        return active;
    }

    public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
    {
        var active = Ready();
        var timer = Stopwatch.StartNew();
        try
        {
            new XDataWriter(targets => new AutoCadXDataWriteTransaction(active, targets), AutoCadSchemaCaches.ValuesFor(active)).WriteSelection(handles, edit);
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
