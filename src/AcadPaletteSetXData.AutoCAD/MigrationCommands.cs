using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using static AcadPaletteSetXData.AutoCAD.SchemaCommands;

[assembly: CommandClass(typeof(AcadPaletteSetXData.AutoCAD.MigrationCommands))]

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// <c>VELEC_MIGRATE</c> and <c>VELEC_CHECK</c> (TZXDATA stage 9). Both read the whole drawing (model space, layouts
/// and blocks) or the pickfirst set, print the report and change the drawing only after confirmation, in one
/// transaction and one Undo step (TZXDATA П14).
/// </summary>
public static class MigrationCommands
{
    /// <summary>Reports longer than this open the text window.</summary>
    private const int TextScreenLines = 24;

    /// <summary>
    /// Converts <c>ESMT_LEP_v1.0</c>, v1 strings of <c>BobrovXDATA</c>, XML <c>VisualTreeString</c> and the earlier
    /// <c>SMARTLINE</c> to XDATA v2 by the <c>migration</c> table of the schema and renames Ids by its <c>rename</c>
    /// (TZXDATA П3). Source data is removed only with «Удалить».
    /// </summary>
    [CommandMethod("VELEC_MIGRATE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public static void Migrate()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        var editor = document.Editor;
        try
        {
            var values = AutoCadSchemaCaches.ValuesFor(document);
            var schema = values.Schema;
            if (schema.Schema is null)
            {
                editor.WriteMessage("\nВ чертеже нет схемы параметров (словарь {0}); выполните VELEC_SCHEMA_IMPORT.\n", DrawingNames.NodRoot);
                return;
            }
            var scan = ScanDrawing(document);
            var choice = Keyword(editor, "\nИсходные данные", "Keep", ("Keep", "Сохранить"), ("Remove", "Удалить"));
            if (choice is null) return;
            var migration = new SchemaMigration(schema, choice == "Remove");
            var results = scan.Writable.Select(item => migration.Migrate(item.Snapshot)).ToArray();
            var lines = SchemaMigration.Report(results, migration.RemoveSources).ToList();
            AddLocked(lines, scan, scan.Locked.Where(item => SchemaMigration.FindSources(item.Snapshot).Count != 0 || HasRenames(migration, item.Snapshot, schema)));
            Show(editor, lines);
            var changed = results.Where(result => result.Changed).Select(result => result.Handle).ToArray();
            if (changed.Length == 0) { editor.WriteMessage("\nПереносить нечего; чертёж не изменён.\n"); return; }
            if (Keyword(editor, "\nВыполнить перенос?", "No", ("Yes", "Да"), ("No", "Нет")) != "Yes") return;
            var written = Write(document, values, scan, changed, ("VELEC_MIGRATE", "Миграция XDATA"), before => migration.Migrate(before).After);
            editor.WriteMessage("\nПеренесено объектов: {0}. Отмена — одной командой U.\n", written);
        }
        catch (System.Exception error) { editor.WriteMessage("\nПеренос не выполнен: {0}\n", error.Message); }
    }

    /// <summary>
    /// Checks duplicates of <c>DEVICE_ID</c>, data outside the schema, <c>Enum</c> keys, the limits of 255 bytes per string
    /// and 16 KB per object and the <c>Persist</c> results; then offers new GUIDs for the copies and the recalculation.
    /// </summary>
    [CommandMethod("VELEC_CHECK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public static void Check()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        var editor = document.Editor;
        try
        {
            var values = AutoCadSchemaCaches.ValuesFor(document);
            var schema = values.Schema;
            if (schema.Schema is null)
                editor.WriteMessage("\nВ чертеже нет схемы параметров (словарь {0}): проверяются дубли {1}, лимиты и прежние форматы.",
                    DrawingNames.NodRoot, XDataV2.DeviceIdKey);
            foreach (var issue in schema.Read.Issues) editor.WriteMessage("\nСхема: {0}", issue);
            var scan = ScanDrawing(document);
            var check = new DrawingCheck(schema);
            var report = check.Run(scan.All.Select(item => item.Snapshot).ToArray());
            var lines = report.Lines().ToList();
            var fixable = report.Targets(DrawingFix.All);
            AddLocked(lines, scan, scan.Locked.Where(item => fixable.Contains(item.Snapshot.Handle, StringComparer.OrdinalIgnoreCase)));
            Show(editor, lines);
            if (fixable.Count == 0) return;
            var choice = Keyword(editor, "\nИсправить", "No", ("Duplicates", "Дубли"), ("Persist", "Persist"), ("All", "Все"), ("No", "Нет"));
            var fix = choice switch { "Duplicates" => DrawingFix.Duplicates, "Persist" => DrawingFix.Persist, "All" => DrawingFix.All, _ => DrawingFix.None };
            var locked = new HashSet<string>(scan.Locked.Select(item => item.Snapshot.Handle), StringComparer.OrdinalIgnoreCase);
            var targets = report.Targets(fix).Where(handle => !locked.Contains(handle)).ToArray();
            if (targets.Length == 0) { editor.WriteMessage("\nЧертёж не изменён.\n"); return; }
            var written = Write(document, values, scan, targets, ("VELEC_CHECK", "Проверка XDATA"), before => check.Fix(before, report, fix));
            foreach (var error in check.FixErrors) editor.WriteMessage("\n  × {0}", error);
            editor.WriteMessage("\nИсправлено объектов: {0}. Отмена — одной командой U.\n", written);
        }
        catch (System.Exception error) { editor.WriteMessage("\nПроверка не выполнена: {0}\n", error.Message); }
    }

    private static bool HasRenames(SchemaMigration migration, EntityDataSnapshot snapshot, SchemaSnapshot schema) =>
        migration.Map.Renames.Count != 0 && XDataV2.Read(snapshot, schema).Values.Any(value => migration.Map.Renames.ContainsKey(value.Id));

    private static void AddLocked(List<string> lines, DrawingScan scan, IEnumerable<Entry> skipped)
    {
        var handles = skipped.Select(item => item.Snapshot.Handle).ToArray();
        if (handles.Length != 0)
            lines.Add("Пропущены объекты на заблокированных слоях (" + handles.Length + "): " + string.Join(", ", handles.Take(20)) + (handles.Length > 20 ? ", …" : "") + ".");
        lines.Insert(0, scan.Selected ? "Объекты: выбранные (" + scan.All.Count + ")." : "Объекты: весь чертёж (" + scan.All.Count + ").");
    }

    private static void Show(Editor editor, IReadOnlyList<string> lines)
    {
        WriteLines(editor, lines);
        if (lines.Count > TextScreenLines) Application.DisplayTextScreen = true;
    }

    /// <summary>Writes the chosen objects in one transaction: an error in any of them writes nothing.</summary>
    private static int Write(Document document, ObjectValuesCache values, DrawingScan scan, IReadOnlyList<string> handles, (string Global, string Local) command,
        Func<EntityDataSnapshot, EntityDataSnapshot> change)
    {
        var ids = handles.Select(handle => scan.All.First(item => string.Equals(item.Snapshot.Handle, handle, StringComparison.OrdinalIgnoreCase)).Id).ToArray();
        var written = new XDataWriter(targets => new AutoCadXDataWriteTransaction(document, targets, ids, command), values).WriteEach(handles, change);
        return written.Count;
    }

    /// <summary>
    /// Detached data of every entity of the drawing, or of the pickfirst set. Objects of external references are not read;
    /// objects on locked layers are read but not written.
    /// </summary>
    private static DrawingScan ScanDrawing(Document document)
    {
        var database = document.Database;
        var implied = document.Editor.SelectImplied();
        var selected = implied.Status == PromptStatus.OK && implied.Value != null ? implied.Value.GetObjectIds() : null;
        var entries = new List<Entry>();
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var lockedLayers = new Dictionary<ObjectId, bool>();
        void Add(ObjectId id)
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) return;
            if (!lockedLayers.TryGetValue(entity.LayerId, out var locked))
                lockedLayers[entity.LayerId] = locked = transaction.GetObject(entity.LayerId, OpenMode.ForRead) is LayerTableRecord layer && layer.IsLocked;
            entries.Add(new Entry(id, AutoCadDataReader.ReadEntity(transaction, entity), locked));
        }
        if (selected != null)
            foreach (var id in selected) Add(id);
        else
        {
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference || block.IsDependent) continue;
                foreach (ObjectId id in block) Add(id);
            }
        }
        transaction.Commit();
        return new DrawingScan(selected != null, entries);
    }

    private sealed class Entry
    {
        public Entry(ObjectId id, EntityDataSnapshot snapshot, bool locked) { Id = id; Snapshot = snapshot; Locked = locked; }
        public ObjectId Id { get; }
        public EntityDataSnapshot Snapshot { get; }
        public bool Locked { get; }
    }

    private sealed class DrawingScan
    {
        public DrawingScan(bool selected, IReadOnlyList<Entry> all) { Selected = selected; All = all; }
        public bool Selected { get; }
        public IReadOnlyList<Entry> All { get; }
        public IEnumerable<Entry> Writable => All.Where(item => !item.Locked);
        public IEnumerable<Entry> Locked => All.Where(item => item.Locked);
    }
}
