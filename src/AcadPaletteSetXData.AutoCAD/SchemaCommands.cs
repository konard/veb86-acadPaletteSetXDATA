using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.UI;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AcadPaletteSetXData.AutoCAD.SchemaCommands))]

namespace AcadPaletteSetXData.AutoCAD;

/// <summary><c>VELEC_SCHEMA_IMPORT</c>, <c>VELEC_SCHEMA_EXPORT</c> (TZXDATA 5.3) and <c>VELEC_SCHEMA_EDIT</c> (TZXDATA 9).</summary>
public static class SchemaCommands
{
    private const string FileFilter = "Схема параметров (*.json)|*.json";

    /// <summary>
    /// The modal schema editor (TZXDATA 9). Edits go to a draft; «Применить» writes it to NOD in one transaction (one
    /// Undo step), «Отмена» leaves the drawing unchanged. Objects of the pickfirst set give the formula preview and
    /// stay selected, so the palette shows new parameters at once.
    /// </summary>
    [CommandMethod("VELEC_SCHEMA_EDIT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public static void Edit()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        var editor = document.Editor;
        try
        {
            var cache = AutoCadSchemaCaches.For(document);
            var read = cache.Get().Read;
            if (read.Issues.Count != 0) WriteLines(editor, read.Issues.Select(issue => "Чертёж: " + issue));
            if (read.IsDamaged)
            {
                editor.WriteMessage("\nСхема в чертеже повреждена; восстановите её командой VELEC_SCHEMA_IMPORT (режим «Заменить»).\n");
                return;
            }
            var implied = editor.SelectImplied();
            var selection = implied.Status == PromptStatus.OK
                ? TemplateCommands.Read(document.Database, implied.Value.GetObjectIds())
                : Array.Empty<EntityDataSnapshot>();
            var model = new SchemaEditorViewModel(read.Schema, ScanUsage(document.Database), selection);
            var window = new SchemaEditorWindow(model, AutoCadThemeSource.Read());
            if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(window) != true)
            {
                editor.WriteMessage("\nРедактор схемы закрыт; чертёж не изменён.\n");
                return;
            }

            SchemaNodWriteResult result;
            using (var transaction = new AutoCadSchemaNodTransaction(document, "VELEC_SCHEMA_EDIT", "Редактор схемы"))
            {
                result = model.Apply(transaction);
            }
            cache.Invalidate();
            editor.WriteMessage(result.Changed
                ? "\nСхема записана: объектов NOD {0}, удалено {1}; {2}. Отмена — одной командой U.\n"
                : "\nСхема чертежа совпадает с черновиком; чертёж не изменён.\n", result.Written.Count, result.Erased.Count, model.Summary);
        }
        catch (System.Exception error) { editor.WriteMessage("\nСхема не записана: {0}\n", error.Message); }
    }

    [CommandMethod("VELEC_SCHEMA_IMPORT", CommandFlags.Modal)]
    public static void Import()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        var editor = document.Editor;
        try { RunImport(document, editor); }
        catch (System.Exception error) { editor.WriteMessage("\nИмпорт схемы не выполнен: {0}\n", error.Message); }
    }

    [CommandMethod("VELEC_SCHEMA_EXPORT", CommandFlags.Modal)]
    public static void Export()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        var editor = document.Editor;
        try
        {
            var read = AutoCadSchemaCaches.For(document).Get().Read;
            WriteLines(editor, read.Issues.Select(issue => issue.ToString()));
            if (read.IsDamaged) { editor.WriteMessage("\nСхема в чертеже повреждена; экспорт отменён.\n"); return; }
            if (read.Schema is null) { editor.WriteMessage("\nВ чертеже нет схемы параметров (словарь {0}).\n", DrawingNames.NodRoot); return; }

            var options = new PromptSaveFileOptions("\nСохранить схему") { Filter = FileFilter, InitialFileName = "schema.json" };
            var file = editor.GetFileNameForSave(options);
            if (file.Status != PromptStatus.OK) return;
            ParameterSchemaJson.WriteFile(file.StringResult, read.Schema);
            editor.WriteMessage("\nСхема записана в {0}: параметров {1}, справочников {2}, шаблонов {3}.\n", file.StringResult,
                read.Schema.Parameters.Count, read.Schema.Enums.Count, read.Schema.Templates.Count);
        }
        catch (System.Exception error) { editor.WriteMessage("\nЭкспорт схемы не выполнен: {0}\n", error.Message); }
    }

    private static void RunImport(Document document, Editor editor)
    {
        var file = editor.GetFileNameForOpen(new PromptOpenFileOptions("\nФайл схемы") { Filter = FileFilter });
        if (file.Status != PromptStatus.OK) return;
        var path = file.StringResult;
        var loaded = ParameterSchemaJson.LoadFile(path);
        WriteLines(editor, loaded.Issues.Select(issue => issue.ToString()));
        if (loaded.Schema is null || loaded.HasErrors) { editor.WriteMessage("\nФайл содержит ошибки; схема не импортирована.\n"); return; }

        var cache = AutoCadSchemaCaches.For(document);
        var current = cache.Get().Read;
        WriteLines(editor, current.Issues.Select(issue => "Чертёж: " + issue));
        SchemaImportMode mode;
        if (current.Schema is null) mode = SchemaImportMode.Replace;
        else if (current.IsDamaged)
        {
            editor.WriteMessage("\nСхема в чертеже повреждена: доступен только режим «Заменить».\n");
            mode = SchemaImportMode.Replace;
        }
        else
        {
            var choice = Keyword(editor, "\nРежим импорта", "Merge", ("Merge", "Объединить"), ("Replace", "Заменить"));
            if (choice is null) return;
            mode = choice == "Merge" ? SchemaImportMode.Merge : SchemaImportMode.Replace;
        }

        var plan = SchemaImport.Plan(current.IsDamaged ? null : current.Schema, loaded.Schema, mode);
        var usage = plan.Current is null ? null : ScanUsage(document.Database);
        if (mode == SchemaImportMode.Merge && plan.Conflicts.Count != 0 && !ResolveConflicts(editor, plan, usage)) return;

        var issues = plan.Validate();
        WriteLines(editor, SchemaImportReport.Format(plan, issues, usage));
        if (issues.Any(issue => issue.Severity == SchemaIssueSeverity.Error))
        {
            editor.WriteMessage("\nРезультат импорта содержит ошибки; схема не импортирована.\n");
            return;
        }
        if (Keyword(editor, "\nЗаписать схему в чертёж?", "No", ("Yes", "Да"), ("No", "Нет")) != "Yes") return;

        SchemaNodWriteResult result;
        using (var transaction = new AutoCadSchemaNodTransaction(document, "VELEC_SCHEMA_IMPORT", "Импорт схемы"))
        {
            result = SchemaNodWriter.Write(transaction, plan.BuildResult(), SchemaNodInfo.Now(Path.GetFileName(path), mode));
        }
        cache.Invalidate();
        editor.WriteMessage(result.Changed
            ? "\nСхема импортирована: записано объектов NOD {0}, удалено {1}. Отмена — одной командой U.\n"
            : "\nСхема чертежа уже совпадает с файлом; чертёж не изменён.\n", result.Written.Count, result.Erased.Count);
    }

    /// <summary>TZXDATA 5.3: a type conflict in Merge is never applied silently.</summary>
    private static bool ResolveConflicts(Editor editor, SchemaImportPlan plan, SchemaUsage? usage)
    {
        editor.WriteMessage("\nКонфликты типов: {0}.\n", plan.Conflicts.Count);
        var all = Keyword(editor, "\nТипы параметров с конфликтом", "Keep", ("Keep", "Оставить"), ("Replace", "Заменить"), ("Each", "Поштучно"));
        if (all is null) return false;
        foreach (var conflict in plan.Conflicts)
        {
            var choice = all;
            if (all == "Each")
            {
                var used = usage is null ? "" : " (объектов: " + usage.Count(SchemaSection.Parameter, conflict.Id) + ")";
                choice = Keyword(editor, "\n" + conflict.Id + ": в чертеже " + conflict.OldType + ", в файле " + conflict.NewType + used,
                    "Keep", ("Keep", "Оставить"), ("Replace", "Заменить"));
                if (choice is null) return false;
            }
            conflict.Resolution = choice == "Replace" ? TypeConflictResolution.Replace : TypeConflictResolution.Keep;
        }
        return true;
    }

    /// <summary>XDATA of every entity of model space, layouts and blocks; used by the report only.</summary>
    private static SchemaUsage ScanUsage(Database database)
    {
        var usage = new SchemaUsage();
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var warnings = new List<string>();
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blocks)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                using var buffer = entity.GetXDataForApplication(DrawingNames.RegApp);
                if (buffer != null) usage.Add(AutoCadDataReader.Copy(buffer, warnings));
            }
        }
        transaction.Commit();
        return usage;
    }

    internal static string? Keyword(Editor editor, string message, string defaultKeyword, params (string Global, string Local)[] keywords)
    {
        var options = new PromptKeywordOptions(message) { AllowNone = true };
        foreach (var keyword in keywords) options.Keywords.Add(keyword.Global, keyword.Local, keyword.Local);
        options.Keywords.Default = defaultKeyword;
        var result = editor.GetKeywords(options);
        if (result.Status == PromptStatus.None) return defaultKeyword;
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    internal static void WriteLines(Editor editor, IEnumerable<string> lines)
    {
        foreach (var line in lines) editor.WriteMessage("\n{0}", line);
        editor.WriteMessage("\n");
    }
}
