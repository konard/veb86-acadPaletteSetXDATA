using System;
using System.Collections.Generic;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// One <see cref="SchemaCache"/> and one <see cref="ObjectValuesCache"/> per document (TZXDATA 10.1, 10.2). The schema cache is dropped when an object of
/// <c>NOD/VELEC</c> (or NOD itself) is modified, erased or restored, after Undo/Redo and when the
/// document is closed; the value cache is cleared with it. Set <c>XDATAPALETTE_TRACE=1</c> to log loads and invalidations.
/// </summary>
internal static class AutoCadSchemaCaches
{
    private static readonly Dictionary<Document, Entry> entries = new Dictionary<Document, Entry>();
    private static bool subscribed;

    public static SchemaCache For(Document document) => EntryFor(document).Cache;

    /// <summary>Value cache of the document (TZXDATA 10.2); it is dropped together with the schema cache.</summary>
    public static ObjectValuesCache ValuesFor(Document document) => EntryFor(document).Values;

    private static Entry EntryFor(Document document)
    {
        if (entries.TryGetValue(document, out var entry)) return entry;
        if (!subscribed)
        {
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            subscribed = true;
        }
        entry = new Entry(document);
        entries.Add(document, entry);
        return entry;
    }

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs args)
    {
        if (!entries.TryGetValue(args.Document, out var entry)) return;
        entries.Remove(args.Document);
        entry.Dispose();
    }

    private sealed class Entry : IDisposable
    {
        private static readonly HashSet<string> UndoCommands =
            new HashSet<string>(new[] { "U", "UNDO", "REDO", "MREDO" }, StringComparer.OrdinalIgnoreCase);

        private readonly Document document;
        private readonly Database database;
        private HashSet<ObjectId> tracked = new HashSet<ObjectId>();

        public Entry(Document document)
        {
            this.document = document;
            database = document.Database;
            Cache = new SchemaCache(Load);
            Values = new ObjectValuesCache(Cache);
            database.ObjectModified += OnObjectChanged;
            database.ObjectErased += OnObjectErased;
            database.ObjectReappended += OnObjectChanged;
            database.ObjectUnappended += OnObjectChanged;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
        }

        public SchemaCache Cache { get; }
        public ObjectValuesCache Values { get; }

        private SchemaNodReadResult Load()
        {
            var snapshot = AutoCadSchemaNod.Read(database);
            tracked = new HashSet<ObjectId>(snapshot.TrackedIds);
            Trace("Кэш схемы загружен: записей " + snapshot.Records.Count + ".");
            return snapshot.ToResult();
        }

        private void OnObjectChanged(object? sender, ObjectEventArgs args) => Check(args.DBObject);

        private void OnObjectErased(object? sender, ObjectErasedEventArgs args) => Check(args.DBObject);

        private void Check(DBObject item)
        {
            if (!Cache.IsLoaded) return;
            if (tracked.Contains(item.ObjectId) || tracked.Contains(item.OwnerId)) Invalidate("изменён объект " + item.Handle);
        }

        private void OnCommandEnded(object? sender, CommandEventArgs args)
        {
            if (UndoCommands.Contains(args.GlobalCommandName)) Invalidate(args.GlobalCommandName);
        }

        private void Invalidate(string reason)
        {
            if (!Cache.IsLoaded) return;
            Cache.Invalidate();
            Trace("Кэш схемы сброшен: " + reason + ".");
        }

        private void Trace(string message)
        {
            if (Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1")
                document.Editor.WriteMessage("\n[XDATAPALETTE] {0}\n", message);
        }

        public void Dispose()
        {
            database.ObjectModified -= OnObjectChanged;
            database.ObjectErased -= OnObjectErased;
            database.ObjectReappended -= OnObjectChanged;
            database.ObjectUnappended -= OnObjectChanged;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
        }
    }
}
