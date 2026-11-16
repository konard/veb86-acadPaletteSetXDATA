using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.RepeatItemCommands;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;
using static AcadPaletteSetXData.Core.Tests.RepeatItemsTests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #61: the program layer of the records — <c>VELEC_ITEM_ADD</c>, <c>VELEC_ITEM_REMOVE_LAST</c> and the LISP
/// functions <c>velec-item-*</c>. AutoCAD passes the arguments as <c>LispDataType</c> codes; the tests build them by hand.
/// </summary>
public sealed class RepeatItemCommandsTests
{
    private static DataValue L(int code, object? value = null) => new(code, value ?? "");

    /// <summary><c>'(("target" . "ЩО1.1") ("cable" "ВВГ"))</c> as AutoCAD passes it.</summary>
    private static DataValue[] ConnCells() => new[]
    {
        L(LispListBegin),
        L(LispListBegin), L(LispText, "target"), L(LispText, "ЩО1.1"), L(LispDottedPair),
        L(LispListBegin), L(LispText, "CABLE"), L(LispText, "ВВГнг 3x1.5"), L(LispListEnd),
        L(LispListEnd),
    };

    [Fact]
    public void CollectionIsFoundByIdOrNameOfTheRecord()
    {
        var schema = RepeatCache().Schema;
        Assert.Equal("Спецификация", Keyword(schema.Parameters.FindRepeat("spec")!));
        Assert.Equal("Соединение", Keyword(schema.Parameters.FindRepeat("conn")!));
        Assert.Equal("spec", Find(schema, "SPEC")?.Id);
        Assert.Equal("conn", Find(schema, "_conn")?.Id); // A macro: ^C^C_VELEC_ITEM_ADD;_conn
        Assert.Equal("conn", Find(schema, "соединение")?.Id);
        Assert.Equal("spec", Find(schema, "Спецификация")?.Id);
        Assert.Equal("conn", Find(schema, "Соединения")?.Id); // The caption of the collection (its group).
        Assert.Null(Find(schema, "Spec_Pos"));
        Assert.Null(Find(schema, ""));
        Assert.Equal("spec — Спецификация/conn — Соединение", Options(schema));
    }

    [Fact]
    public void LispArgumentsAreSplitAndTheFieldsAreParsed()
    {
        var repeat = RepeatCache().Schema.Parameters.FindRepeat("conn")!;
        var arguments = new[] { L(LispObjectId, 42), L(LispText, "conn") }.Concat(ConnCells()).ToArray();
        var split = Arguments(arguments);
        Assert.Equal(3, split.Count);
        Assert.Equal(42, split[0].Single().Value);
        Assert.Equal("conn", split[1].Single().Value);
        Assert.Equal(10, split[2].Count);

        var cells = Cells(repeat, split[2]);
        Assert.Equal(new[] { "target", "cable" }, cells.Select(cell => cell.Key)); // The spelling of the schema.
        Assert.Equal(new object[] { "ЩО1.1", "ВВГнг 3x1.5" }, cells.Select(cell => cell.Value));
        Assert.Empty(Cells(repeat, new[] { L(LispNil) }));
        Assert.Empty(Cells(repeat, null));

        var unknown = Assert.Throws<ArgumentException>(() => Cells(repeat, new[]
        {
            L(LispListBegin), L(LispListBegin), L(LispText, "length"), L(LispDouble, 5.0), L(LispDottedPair), L(LispListEnd),
        }));
        Assert.Contains("нет поля «length» (поля: target, cable)", unknown.Message);
        Assert.Throws<ArgumentException>(() => Cells(repeat, new[] { L(LispText, "target") }));

        Assert.Equal(2, Number(new[] { L(LispInt16, (short)2) }));
        Assert.Equal(3, Number(new[] { L(LispInt32, 3) }));
        Assert.Equal(4, Number(new[] { L(LispDouble, 4.0) }));
        Assert.Throws<ArgumentException>(() => Number(new[] { L(LispDouble, 1.5) }));
        Assert.Throws<ArgumentException>(() => Number(new[] { L(LispInt16, (short)0) }));
    }

    [Fact]
    public void LispFunctionsAddCountGetAndRemoveTheLastRecord()
    {
        var cache = RepeatCache();
        var drawing = new Drawing(cache, Device("A", Spec(1, "1", "Светильник (корпус)", 1).Concat(Spec(2, "2", "Лампа", 2)).ToArray()));
        var conn = cache.Schema.Parameters.FindRepeat("conn")!;
        var spec = cache.Schema.Parameters.FindRepeat("spec")!;
        ObjectValues Values() => drawing.Read(drawing.Snapshots[0]);

        // (velec-item-count ent "conn") → 0; (velec-item-add ent "conn" '(("target" . "ЩО1.1") ("cable" "ВВГнг 3x1.5"))) → 1
        Assert.Equal(0, Count(Values(), conn));
        Assert.Equal(1, Last(Values(), conn) + 1);
        drawing.WriteSelection(new[] { "A" }, SelectionEdit.AddItem(conn.Id!, Cells(conn, ConnCells())));
        Assert.Equal(1, Count(Values(), conn));
        Assert.Equal(new object?[] { "ЩО1.1" }, drawing.Stored("conn_1_target"));

        // (velec-item-get ent "spec" 2) → (("pos" . "2") ("Name" . "Лампа") ("col" . 2.0))
        var record = Get(Values(), spec, 2)!;
        Assert.Equal(new[] { "pos", "Name", "col" }, record.Select(pair => pair.Key));
        Assert.Equal(new object[] { "2", "Лампа", 2.0 }, record.Select(pair => pair.Value));
        Assert.Null(Get(Values(), spec, 3));
        var lisp = ToLisp(record);
        Assert.Equal(new[]
        {
            LispListBegin,
            LispListBegin, LispText, LispText, LispDottedPair,
            LispListBegin, LispText, LispText, LispDottedPair,
            LispListBegin, LispText, LispDouble, LispDottedPair,
            LispListEnd,
        }, lisp.Select(value => value.TypeCode));
        Assert.Equal(2.0, lisp[11].Value);
        Assert.Equal(new[] { LispInt16, LispInt32 }, ToLisp(new[]
        {
            new KeyValuePair<string, object>("on", true), new KeyValuePair<string, object>("n", 7),
        }).Where(value => value.TypeCode != LispText && value.TypeCode >= LispInt16 && value.TypeCode <= LispInt32).Select(value => value.TypeCode));

        // (velec-item-remove-last ent "spec") → 2
        Assert.Equal(2, Last(Values(), spec));
        drawing.WriteSelection(new[] { "A" }, SelectionEdit.RemoveLastItem(spec.Id!));
        Assert.Equal(1, Count(Values(), spec));
        Assert.Null(Get(Values(), spec, 2));
    }

    [Fact]
    public void CommandLinesNameTheRecordsAndTheObjects()
    {
        var schema = RepeatCache().Schema;
        var conn = schema.Parameters.FindRepeat("conn")!;
        Assert.Equal("Удалено: Соединение №2 (2 объекта)", Summary("Удалено", conn, new[] { 2, 2 }));
        Assert.Equal("Удалено: Соединение №3 (1 объект), Соединение №2 (1 объект)", Summary("Удалено", conn, new[] { 2, 0, 3 }));
        Assert.Equal("Добавлено: Соединение №1 (5 объектов)", Summary("Добавлено", conn, Enumerable.Repeat(1, 5)));
        Assert.Equal("21 объект", ObjectsText(21));
        Assert.Equal("12 объектов", ObjectsText(12));

        var spec = schema.Parameters.FindRepeat("spec")!;
        Assert.Equal(new[] { "Позиция (pos) <пусто>", "Наименование (Name) <пусто>", "Кол. (col) <1>" },
            spec.Fields!.Select(field => FieldPrompt(schema, spec, field)));
    }

    [Fact]
    public void ARecordThatDoesNotFitIsNotAddedAndTheMessageSaysWhy()
    {
        // VELEC_ITEM_ADD and velec-item-add write through XDataWriter: 16 KB of XDATA is checked for the whole selection.
        var cache = RepeatCache();
        var name = new string('Ж', 120);
        var records = Enumerable.Range(1, 200).SelectMany(n => Spec(n, n.ToString(System.Globalization.CultureInfo.InvariantCulture), name, 1)).ToArray();
        var full = Device("A", records);
        var size = XDataV2.Read(full, cache.Schema).XDataSize;
        var count = Enumerable.Range(1, 200).Last(n => XDataV2.Read(Device("A", records.Take(3 * n).ToArray()), cache.Schema).XDataSize <= XDataV2.MaxXDataBytes - 200);
        Assert.True(size > XDataV2.MaxXDataBytes);
        var drawing = new Drawing(cache, Device("A", records.Take(3 * count).ToArray()), Device("B"));
        var spec = cache.Schema.Parameters.FindRepeat("spec")!;
        var error = Record.Exception(() =>
        {
            for (var i = 0; i < 10; i++)
                drawing.WriteSelection(new[] { "A", "B" }, SelectionEdit.AddItem(spec.Id!, new[] { new KeyValuePair<string, object>("Name", name) }));
        });
        Assert.NotNull(error);
        Assert.Contains("16", error!.Message);
        // The failed write left both objects as they were: B got as many records as A.
        Assert.Equal(Count(drawing.Read(drawing.Snapshots[1]), spec), Count(drawing.Read(drawing.Snapshots[0]), spec) - count);
    }
}
