using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AcadPaletteSetXData.AutoCAD.ItemCommands))]

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// Records of a <c>Repeat</c> from commands and LISP (issue #61, docs/repeating-parameters.md, section 4): every write
/// goes through <see cref="XDataWriter"/> in one transaction and one Undo step, with the checks of types, 255 bytes and
/// 16 KB. «Последняя» is the record with the largest number of each object.
/// </summary>
public static class ItemCommands
{
    /// <summary><c>VELEC_ITEM_ADD</c>: a record with the next number of each selected object; Enter in a field — its default.</summary>
    [CommandMethod("VELEC_ITEM_ADD", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public static void Add() => Run(add: true);

    /// <summary><c>VELEC_ITEM_REMOVE_LAST</c>: removes the record with the largest number of each selected object.</summary>
    [CommandMethod("VELEC_ITEM_REMOVE_LAST", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
    public static void RemoveLast() => Run(add: false);

    private static void Run(bool add)
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        var editor = document.Editor;
        try
        {
            var values = AutoCadSchemaCaches.ValuesFor(document);
            var schema = values.Schema;
            if (!HasRepeats(editor, schema)) return;
            var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = add ? "\nОбъекты для новой записи" : "\nОбъекты, у которых удалить последнюю запись" });
            if (selection.Status != PromptStatus.OK) return;
            var ids = selection.Value.GetObjectIds();
            var repeat = AskCollection(editor, schema);
            if (repeat is null) return;
            var cells = new List<KeyValuePair<string, object>>();
            if (add)
                foreach (var field in repeat.Fields!)
                {
                    var answer = editor.GetString(new PromptStringOptions("\n" + RepeatItemCommands.FieldPrompt(schema, repeat, field)) { AllowSpaces = true });
                    if (answer.Status != PromptStatus.OK) return;
                    // Enter keeps the default of the field (XDataPatch fills it).
                    if (answer.StringResult.Length != 0) cells.Add(new KeyValuePair<string, object>(field.Suffix, answer.StringResult));
                }
            var objects = values.Get(TemplateCommands.Read(document.Database, ids));
            var numbers = objects.Select(item => RepeatItemCommands.Last(item, repeat) + (add ? 1 : 0)).ToArray();
            if (!add && numbers.All(number => number == 0))
            {
                editor.WriteMessage("\nУ выбранных объектов нет записей «{0}»; чертёж не изменён.\n", RepeatItems.CollectionCaption(repeat));
                return;
            }
            var edit = add ? SelectionEdit.AddItem(repeat.Id!, cells) : SelectionEdit.RemoveLastItem(repeat.Id!);
            Write(document, ids, add, values, edit);
            editor.WriteMessage("\n{0}. Отмена — одной командой U.\n", RepeatItemCommands.Summary(add ? "Добавлено" : "Удалено", repeat, numbers));
        }
        catch (System.Exception error) { editor.WriteMessage(add ? "\nЗапись не добавлена: {0}\n" : "\nЗапись не удалена: {0}\n", error.Message); }
    }

    /// <summary><c>(velec-item-add ent "conn" '(("target" . "ЩО1.1")))</c> → the number of the new record, or nil with a message.</summary>
    [LispFunction("velec-item-add")]
    public static object? LispAdd(ResultBuffer? arguments) => Lisp("velec-item-add", arguments, 2, 3, (document, id, item, repeat, rest) =>
    {
        var cells = RepeatItemCommands.Cells(repeat, rest.Count == 0 ? null : rest[0]);
        var number = RepeatItemCommands.Last(item, repeat) + 1;
        Write(document, new[] { id }, true, AutoCadSchemaCaches.ValuesFor(document), SelectionEdit.AddItem(repeat.Id!, cells));
        return number;
    });

    /// <summary><c>(velec-item-remove-last ent "conn")</c> → the number of the removed record, or nil when there is none.</summary>
    [LispFunction("velec-item-remove-last")]
    public static object? LispRemoveLast(ResultBuffer? arguments) => Lisp("velec-item-remove-last", arguments, 2, 2, (document, id, item, repeat, rest) =>
    {
        var number = RepeatItemCommands.Last(item, repeat);
        if (number == 0) return null;
        Write(document, new[] { id }, false, AutoCadSchemaCaches.ValuesFor(document), SelectionEdit.RemoveLastItem(repeat.Id!));
        return number;
    });

    /// <summary><c>(velec-item-count ent "conn")</c> → the number of records.</summary>
    [LispFunction("velec-item-count")]
    public static object? LispCount(ResultBuffer? arguments) =>
        Lisp("velec-item-count", arguments, 2, 2, (document, id, item, repeat, rest) => RepeatItemCommands.Count(item, repeat));

    /// <summary><c>(velec-item-get ent "spec" 2)</c> → <c>(("pos" . "2") ("Name" . "Лампа") ("col" . 2.0))</c>, or nil.</summary>
    [LispFunction("velec-item-get")]
    public static object? LispGet(ResultBuffer? arguments) => Lisp("velec-item-get", arguments, 3, 3, (document, id, item, repeat, rest) =>
    {
        var record = RepeatItemCommands.Get(item, repeat, RepeatItemCommands.Number(rest[0]));
        return record is null ? null : new ResultBuffer(RepeatItemCommands.ToLisp(record).Select(value => value.TypeCode == RepeatItemCommands.LispListBegin ||
            value.TypeCode == RepeatItemCommands.LispListEnd || value.TypeCode == RepeatItemCommands.LispDottedPair
                ? new TypedValue(value.TypeCode) : new TypedValue(value.TypeCode, value.Value)).ToArray());
    });

    /// <summary>
    /// The common part of the LISP functions: <c>ename</c>, the collection and <paramref name="min"/>..<paramref name="max"/>
    /// arguments in all. An error is written to the command line and the function returns nil.
    /// </summary>
    private static object? Lisp(string name, ResultBuffer? arguments, int min, int max,
        Func<Document, ObjectId, ObjectValues, SchemaParameter, IReadOnlyList<IReadOnlyList<DataValue>>, object?> body)
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return null;
        try
        {
            var list = RepeatItemCommands.Arguments(arguments?.AsArray().Select(value => new DataValue(value.TypeCode, value.Value ?? "")).ToArray());
            if (list.Count < min || list.Count > max)
                throw new ArgumentException("ожидается " + (min == max ? min.ToString() : min + "–" + max) + " аргумента: (" + name + " ename \"набор\"" +
                    (name == "velec-item-get" ? " номер" : name == "velec-item-add" ? " [поля]" : "") + ").");
            if (list[0].Count != 1 || !(list[0][0].Value is ObjectId id) || id.IsNull || id.IsErased)
                throw new ArgumentException("первый аргумент — объект чертежа (ename).");
            if (list[1].Count != 1 || !(list[1][0].Value is string collection)) throw new ArgumentException("второй аргумент — Id набора записей, например \"spec\".");
            var values = AutoCadSchemaCaches.ValuesFor(document);
            var schema = values.Schema;
            if (schema.Schema is null) throw new InvalidOperationException("в чертеже нет схемы параметров; выполните VELEC_SCHEMA_IMPORT.");
            var repeat = RepeatItemCommands.Find(schema, collection)
                ?? throw new ArgumentException("в схеме нет набора записей «" + collection + "» (есть: " + RepeatItemCommands.Options(schema) + ").");
            var snapshot = TemplateCommands.Read(document.Database, new[] { id }).SingleOrDefault()
                ?? throw new ArgumentException("объект не является примитивом чертежа.");
            return body(document, id, values.Get(snapshot), repeat, list.Skip(2).ToArray());
        }
        catch (System.Exception error)
        {
            document.Editor.WriteMessage("\n{0}: {1}\n", name, error.Message);
            return null;
        }
    }

    private static void Write(Document document, IReadOnlyList<ObjectId> ids, bool add, ObjectValuesCache values, SelectionEdit edit)
    {
        var handles = ids.Select(item => item.Handle.ToString()).ToArray();
        var command = add ? ("VELEC_ITEM_ADD", "Добавление записи") : ("VELEC_ITEM_REMOVE_LAST", "Удаление последней записи");
        // One transaction for the whole selection: a record that does not fit 16 KB of any object writes nothing.
        new XDataWriter(targets => new AutoCadXDataWriteTransaction(document, targets, ids, command), values).WriteSelection(handles, edit);
    }

    private static bool HasRepeats(Editor editor, SchemaSnapshot schema)
    {
        if (schema.Schema is null)
        {
            editor.WriteMessage("\nВ чертеже нет схемы параметров (словарь {0}); выполните VELEC_SCHEMA_IMPORT.\n", DrawingNames.NodRoot);
            return false;
        }
        if (schema.Parameters.Repeats.Count != 0) return true;
        editor.WriteMessage("\nВ схеме нет наборов записей (параметров типа Repeat).\n");
        return false;
    }

    /// <summary>The collection: its Id or name; with one collection in the schema Enter chooses it.</summary>
    private static SchemaParameter? AskCollection(Editor editor, SchemaSnapshot schema)
    {
        var single = schema.Parameters.Repeats.Count == 1 ? schema.Parameters.Repeats[0] : null;
        while (true)
        {
            var answer = editor.GetString(new PromptStringOptions("\nНабор записей [" + RepeatItemCommands.Options(schema) + "]" +
                (single is null ? "" : " <" + single.Id + ">")) { AllowSpaces = false });
            if (answer.Status != PromptStatus.OK) return null;
            if (answer.StringResult.Length == 0) { if (single != null) return single; continue; }
            var found = RepeatItemCommands.Find(schema, answer.StringResult);
            if (found != null) return found;
            editor.WriteMessage("\nНет такого набора записей: {0}.", answer.StringResult);
        }
    }
}
