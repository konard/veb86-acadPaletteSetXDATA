using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Internal.PropertyInspector;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// Issue #66: the objects of the «Навигатор» tab. Reads the BobrovXDATA objects of the ModelSpace of the active document
/// (ForRead, under the document lock and one read transaction) with the reader of the selection and takes their values from
/// the value cache of the document, so unchanged XDATA is not parsed again. Each document keeps its own objects; after the
/// first read only the objects changed since the last read are read again. <see cref="Changed"/> is raised on Idle after a
/// command changed the drawing, after Undo/Redo, a change of the schema, when another document becomes active or the
/// document is closed. The source does not follow the selection of AutoCAD: it only sets it when a leaf is chosen.
/// Set <c>XDATAPALETTE_TRACE=1</c> to log reads.
/// </summary>
internal sealed class AutoCadNavigatorSource : INavigatorSource, IDisposable
{
    /// <summary>Above this number of changed nested objects (attributes, XRecords) the next read reads every object.</summary>
    private const int NestedLimit = 2000;
    /// <summary>Owners walked up from a changed XRecord to its object (XRecord → dictionary → extension dictionary → object).</summary>
    private const int NestedDepth = 4;
    private static readonly HashSet<string> UndoCommands =
        new HashSet<string>(new[] { "U", "UNDO", "REDO", "MREDO" }, StringComparer.OrdinalIgnoreCase);

    private readonly DocumentCollection documents = AcApplication.DocumentManager;
    private readonly Dictionary<Document, State> states = new Dictionary<Document, State>();
    private readonly Dictionary<string, string?> typeNames = new Dictionary<string, string?>(StringComparer.Ordinal);
    private readonly bool traceEnabled = Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1";
    private Document? lastRead;
    private Document? closing;
    private bool idleArmed;
    private bool disposed;
    private bool displayNameFailed;
    private int serial;

    public AutoCadNavigatorSource()
    {
        documents.DocumentActivated += OnDocumentActivated;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
    }

    public event EventHandler? Changed;

    public NavigatorSnapshot Read(bool full)
    {
        var active = disposed ? null : documents.MdiActiveDocument;
        if (active == null || ReferenceEquals(active, closing))
        {
            lastRead = null;
            return NavigatorSnapshot.Empty;
        }
        var state = StateFor(active);
        lastRead = active;
        var timer = Stopwatch.StartNew();
        var all = full || !state.Loaded || state.Full;
        int read;
        NavigatorItem[] items;
        SchemaSnapshot schema;
        var failed = 0;
        using (active.LockDocument())
        {
            var database = active.Database;
            using var transaction = database.TransactionManager.StartOpenCloseTransaction();
            var reader = new AutoCadDataReader(transaction);
            var modelSpace = SymbolUtilityServices.GetBlockModelSpaceId(database);
            if (state.ModelSpace != modelSpace) { state.ModelSpace = modelSpace; all = true; }
            if (all)
            {
                state.Objects.Clear();
                state.Failed.Clear();
                var ids = new List<ObjectId>();
                var space = (BlockTableRecord)transaction.GetObject(modelSpace, OpenMode.ForRead);
                foreach (ObjectId id in space) ids.Add(id);
                foreach (var id in ids) ReadObject(state, transaction, reader, id);
                read = ids.Count;
            }
            else
            {
                var ids = new HashSet<ObjectId>(state.Dirty);
                foreach (var id in state.Nested) Resolve(state, transaction, id, ids);
                foreach (var id in ids) ReadObject(state, transaction, reader, id);
                read = ids.Count;
            }
            state.Dirty.Clear();
            state.Nested.Clear();
            state.Full = false;
            state.Loaded = true;
            state.Pending = false;
            // The schema is loaded and XDATA v2 is parsed under the same lock; unchanged objects come from the cache.
            var values = AutoCadSchemaCaches.ValuesFor(active);
            schema = values.Schema;
            var list = new List<NavigatorItem>(state.Objects.Count);
            foreach (var entry in state.Objects.Values)
            {
                try { list.Add(new NavigatorItem(values.Get(entry.Snapshot), entry.TypeName)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // One object that cannot be parsed does not hide the others.
                    failed++;
                    Trace(active, "Объект [" + entry.Snapshot.Handle + "] не разобран: " + error.Message);
                }
            }
            items = list.ToArray();
            if (traceEnabled)
                Trace(active, string.Format(CultureInfo.InvariantCulture,
                    "Навигатор: {0} чтение, просмотрено {1}, объектов BobrovXDATA {2}, ошибок {3}; {4} мс; кэш значений {5}, разборов XDATA {6}.",
                    all ? "полное" : "частичное", read, items.Length, state.Failed.Count + failed, timer.ElapsedMilliseconds, values.Count, values.ParseCount));
        }
        return new NavigatorSnapshot(state.Key, items, schema, null, state.Failed.Count + failed);
    }

    /// <summary>Reads one object of the ModelSpace again: an erased object, an object of another space or without BobrovXDATA is dropped.</summary>
    private void ReadObject(State state, Transaction transaction, AutoCadDataReader reader, ObjectId id)
    {
        state.Objects.Remove(id);
        state.Failed.Remove(id);
        if (id.IsNull || !id.IsValid || id.IsErased) return;
        try
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity entity) || entity.OwnerId != state.ModelSpace) return;
            using (var data = entity.GetXDataForApplication(DrawingNames.RegApp))
            {
                if (data == null) return;
            }
            var typeName = TypeDisplayName(entity);
            state.Objects[id] = new Entry(reader.ReadEntity(entity, typeDisplayName: typeName), typeName);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Issue #66: an object that cannot be read is counted; the others are shown.
            state.Failed.Add(id);
            if (traceEnabled) Trace(documents.MdiActiveDocument, "Объект [" + id.Handle + "] не прочитан: " + error.Message);
        }
    }

    /// <summary>An attribute or an XRecord changed: the object of the ModelSpace it belongs to is read again.</summary>
    private static void Resolve(State state, Transaction transaction, ObjectId id, ISet<ObjectId> objects)
    {
        var current = id;
        for (var depth = 0; depth <= NestedDepth; depth++)
        {
            if (current.IsNull || !current.IsValid) return;
            DBObject item;
            try { item = transaction.GetObject(current, OpenMode.ForRead, true); }
            catch (AcException) { return; }
            if (item is Entity && item.OwnerId == state.ModelSpace)
            {
                objects.Add(current);
                return;
            }
            current = item.OwnerId;
        }
    }

    /// <summary>
    /// The name the Properties palette shows for the object type (issue #41), one call of the Object Property Manager per
    /// class; null when AutoCAD gives no name (the tree then shows the .NET type).
    /// </summary>
    private string? TypeDisplayName(Entity entity)
    {
        var key = entity.GetRXClass().Name;
        if (typeNames.TryGetValue(key, out var known)) return known;
        string? name = null;
        try { name = ObjectPropertyManagerProperties.GetDisplayName(entity); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!displayNameFailed) System.Diagnostics.Trace.WriteLine(error, "XDATAPALETTE type display name");
            displayNameFailed = true;
        }
        if (string.IsNullOrWhiteSpace(name)) name = null;
        typeNames[key] = name;
        return name;
    }

    /// <summary>
    /// Selects the leaf object through the standard PICKFIRST set of AutoCAD (<c>Editor.SetImpliedSelection</c>);
    /// <paramref name="zoom"/> also shows its extents. False when the handle is unknown or the object is erased.
    /// </summary>
    public bool Select(string handle, bool zoom)
    {
        var active = disposed ? null : documents.MdiActiveDocument;
        if (active == null || ReferenceEquals(active, closing))
            throw new InvalidOperationException("Нет активного чертежа.");
        if (!active.Editor.IsQuiescent)
            throw new InvalidOperationException("Выполняется команда; завершите её и повторите выбор.");
        long value;
        try { value = Convert.ToInt64(handle, 16); }
        catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException) { return false; }
        using (active.LockDocument())
        {
            var database = active.Database;
            if (!database.TryGetObjectId(new Handle(value), out var id) || id.IsNull || !id.IsValid || id.IsErased) return false;
            var editor = active.Editor;
            editor.SetImpliedSelection(new[] { id });
            if (zoom) Zoom(active, id);
            if (traceEnabled) Trace(active, "Навигатор: выбран объект [" + handle + "]" + (zoom ? " с показом." : "."));
        }
        return true;
    }

    /// <summary>Shows the extents of the object in the current view of the model with a margin; the layout switches to Model.</summary>
    private static void Zoom(Document document, ObjectId id)
    {
        var database = document.Database;
        Extents3d extents;
        using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) return;
            try { extents = entity.GeometricExtents; }
            catch (AcException) { return; } // An object without geometry is only selected.
        }
        if (!database.TileMode) LayoutManager.Current.CurrentLayout = "Model";
        var editor = document.Editor;
        using var view = editor.GetCurrentView();
        var worldToEye = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                          Matrix3d.Displacement(view.Target - Point3d.Origin) *
                          Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
        extents.TransformBy(worldToEye);
        var width = extents.MaxPoint.X - extents.MinPoint.X;
        var height = extents.MaxPoint.Y - extents.MinPoint.Y;
        // A point or a tiny object keeps the size of the view it is shown in.
        var size = Math.Max(width, height);
        var minimum = Math.Max(view.Height, view.Width) / 20;
        if (size < minimum)
        {
            width = Math.Max(width, minimum);
            height = Math.Max(height, minimum);
        }
        const double Margin = 1.5;
        var ratio = view.Width / view.Height;
        if (width / height > ratio) height = width / ratio;
        else width = height * ratio;
        view.CenterPoint = new Point2d((extents.MinPoint.X + extents.MaxPoint.X) / 2, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2);
        view.Width = width * Margin;
        view.Height = height * Margin;
        editor.SetCurrentView(view);
    }

    private State StateFor(Document document)
    {
        if (states.TryGetValue(document, out var state)) return state;
        state = new State(this, document, document.Name + "#" + (++serial).ToString(CultureInfo.InvariantCulture));
        states.Add(document, state);
        return state;
    }

    private void OnDocumentActivated(object sender, DocumentCollectionEventArgs args)
    {
        if (!ReferenceEquals(args.Document, closing)) closing = null;
        Arm();
    }

    /// <summary>The closed document is forgotten at once: the navigator drops its objects before the next document is read.</summary>
    private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
    {
        closing = args.Document;
        if (states.TryGetValue(args.Document, out var state))
        {
            states.Remove(args.Document);
            state.Dispose();
        }
        if (ReferenceEquals(lastRead, args.Document))
        {
            lastRead = null;
            // Read sees the closing document and returns no objects; no transaction is opened.
            Changed?.Invoke(this, EventArgs.Empty);
        }
        Arm();
    }

    /// <summary>Waits for Idle once: many changes of one command give one <see cref="Changed"/>.</summary>
    private void Arm()
    {
        if (disposed || idleArmed) return;
        idleArmed = true;
        AcApplication.Idle += OnIdle;
    }

    private void OnIdle(object? sender, EventArgs args)
    {
        if (disposed) return;
        var active = documents.MdiActiveDocument;
        if (active != null && !active.Editor.IsQuiescent) return;
        AcApplication.Idle -= OnIdle;
        idleArmed = false;
        var relevant = !ReferenceEquals(active, lastRead) || (active != null && states.TryGetValue(active, out var state) && state.Pending);
        if (relevant) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trace(Document? document, string message)
    {
        if (traceEnabled) document?.Editor.WriteMessage("\n[XDATAPALETTE] {0}\n", message);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (idleArmed) AcApplication.Idle -= OnIdle;
        idleArmed = false;
        documents.DocumentActivated -= OnDocumentActivated;
        documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        foreach (var state in states.Values) state.Dispose();
        states.Clear();
        lastRead = null;
        closing = null;
    }

    private sealed class Entry
    {
        public Entry(EntityDataSnapshot snapshot, string? typeName) { Snapshot = snapshot; TypeName = typeName; }
        public EntityDataSnapshot Snapshot { get; }
        public string? TypeName { get; }
    }

    /// <summary>
    /// The objects read of one document and the identities changed since then. Database events only record identities;
    /// objects are opened by the next <see cref="Read"/>, on Idle.
    /// </summary>
    private sealed class State : IDisposable
    {
        private readonly AutoCadNavigatorSource owner;
        private readonly Document document;
        private readonly Database database;
        private readonly SchemaCache schema;

        public State(AutoCadNavigatorSource owner, Document document, string key)
        {
            this.owner = owner;
            this.document = document;
            database = document.Database;
            Key = key;
            ModelSpace = SymbolUtilityServices.GetBlockModelSpaceId(database);
            schema = AutoCadSchemaCaches.For(document);
            database.ObjectAppended += OnObjectChanged;
            database.ObjectModified += OnObjectChanged;
            database.ObjectErased += OnObjectErased;
            database.ObjectReappended += OnObjectChanged;
            database.ObjectUnappended += OnObjectChanged;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            schema.Invalidated += OnSchemaInvalidated;
        }

        /// <summary>The key of the document in <see cref="NavigatorSnapshot.Document"/>; a reopened drawing gets a new one.</summary>
        public string Key { get; }
        public ObjectId ModelSpace { get; set; }
        /// <summary>BobrovXDATA objects of the ModelSpace in the order they were read.</summary>
        public Dictionary<ObjectId, Entry> Objects { get; } = new Dictionary<ObjectId, Entry>();
        public HashSet<ObjectId> Failed { get; } = new HashSet<ObjectId>();
        public HashSet<ObjectId> Dirty { get; } = new HashSet<ObjectId>();
        public HashSet<ObjectId> Nested { get; } = new HashSet<ObjectId>();
        public bool Loaded { get; set; }
        public bool Full { get; set; }
        /// <summary>Something changed since the last read: Idle raises <see cref="Changed"/>.</summary>
        public bool Pending { get; set; }

        private void OnObjectChanged(object? sender, ObjectEventArgs args) => Record(args.DBObject);

        private void OnObjectErased(object? sender, ObjectErasedEventArgs args) => Record(args.DBObject);

        private void Record(DBObject item)
        {
            if (!Loaded) return;
            if (item is Entity && item.OwnerId == ModelSpace) Dirty.Add(item.ObjectId);
            else if (item is AttributeReference) Dirty.Add(item.OwnerId);
            else if (item is Xrecord || item is DBDictionary)
            {
                if (Nested.Count < NestedLimit) Nested.Add(item.ObjectId);
                else Full = true;
            }
            else return;
            Notify();
        }

        private void OnCommandEnded(object? sender, CommandEventArgs args)
        {
            if (!Loaded || !UndoCommands.Contains(args.GlobalCommandName)) return;
            // Undo/Redo restores objects in ways events do not describe exactly: every object is read again.
            Full = true;
            Notify();
        }

        // The values are parsed again against the new schema (the value cache was cleared with it).
        private void OnSchemaInvalidated(object? sender, EventArgs args)
        {
            if (Loaded) Notify();
        }

        private void Notify()
        {
            Pending = true;
            owner.Arm();
        }

        public void Dispose()
        {
            database.ObjectAppended -= OnObjectChanged;
            database.ObjectModified -= OnObjectChanged;
            database.ObjectErased -= OnObjectErased;
            database.ObjectReappended -= OnObjectChanged;
            database.ObjectUnappended -= OnObjectChanged;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            schema.Invalidated -= OnSchemaInvalidated;
            Objects.Clear();
            Dirty.Clear();
            Nested.Clear();
        }
    }
}
