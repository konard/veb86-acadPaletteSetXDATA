using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AcadPaletteSetXData.AutoCAD.TemplateCommands))]

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// <c>VELEC_TEMPLATE_APPLY</c> (TZXDATA 5.3, 6.4, 6.6): assigns a template of the schema to the selected objects in one
/// transaction and one Undo step; objects without <c>DEVICE_ID</c> get a new GUID, values outside the template are kept.
/// </summary>
public static class TemplateCommands
{
    [CommandMethod("VELEC_TEMPLATE_APPLY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public static void Apply()
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
            var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nОбъекты для назначения шаблона" });
            if (selection.Status != PromptStatus.OK) return;
            var ids = selection.Value.GetObjectIds();
            var prompt = new TemplateApply(schema, values.Get(Read(document.Database, ids)));
            if (prompt.Templates.Count == 0 && !prompt.HasTemplate)
            {
                var types = string.Join(", ", prompt.Objects.Select(item => item.EntityType).Distinct(StringComparer.OrdinalIgnoreCase));
                editor.WriteMessage("\nВ схеме нет шаблона для всех выбранных типов объектов ({0}).\n", types);
                return;
            }
            foreach (var line in prompt.Lines()) editor.WriteMessage("\n  {0}", line);
            string? id;
            while (true)
            {
                var answer = editor.GetString(new PromptStringOptions("\nШаблон (Id или номер" + (prompt.HasTemplate ? ", " + TemplateApply.None + " — без шаблона" : "") + ")")
                    { AllowSpaces = false });
                if (answer.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(answer.StringResult)) return;
                id = prompt.Parse(answer.StringResult);
                if (id != null) break;
                editor.WriteMessage("\nНет такого шаблона для выбранных объектов: {0}.", answer.StringResult);
            }
            var changes = prompt.Changes(id);
            if (changes == 0) { editor.WriteMessage("\nУ всех объектов уже назначен шаблон {0}; чертёж не изменён.\n", id); return; }
            var handles = ids.Select(item => item.Handle.ToString()).ToArray();
            // One transaction for the whole selection: an error in any object (locked layer, v1 format, a wrong type) writes nothing.
            new XDataWriter(targets => new AutoCadXDataWriteTransaction(document, targets, ids), values).WriteSelection(handles, SelectionEdit.SetTemplate(id));
            editor.WriteMessage(id.Length == 0
                ? "\nШаблон снят у объектов: {1}; значения сохранены. Отмена — одной командой U.\n"
                : "\nШаблон {0} назначен объектам: {1}. Отмена — одной командой U.\n", id, changes);
        }
        catch (System.Exception error) { editor.WriteMessage("\nШаблон не назначен: {0}\n", error.Message); }
    }

    /// <summary>Detached snapshots of the chosen objects, as the palette reads them.</summary>
    internal static IReadOnlyList<EntityDataSnapshot> Read(Database database, IEnumerable<ObjectId> ids)
    {
        var snapshots = new List<EntityDataSnapshot>();
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        foreach (var id in ids)
            if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity) snapshots.Add(AutoCadDataReader.ReadEntity(transaction, entity));
        transaction.Commit();
        return snapshots;
    }
}
