using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Node = AcadPaletteSetXData.Core.XDataV2.Tokens.Node;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Records of a <c>Repeat</c> (issue #61, option B): «+ Добавить», «Удалить последнюю», deletion with renumbering and
/// moving by swapping numbers. Every operation works per object: «последняя» is the largest number of that object.
/// </summary>
public sealed partial class XDataPatch
{
    /// <summary>Changes the records of one object; null when the object is left as it is.</summary>
    private SchemaParameter? EditItems(List<Node> nodes, SelectionEdit edit, SchemaSnapshot schema, string name)
    {
        var repeat = schema.Parameters.FindRepeat(edit.Field)
            ?? throw new InvalidOperationException("Параметр " + edit.Field + " не является набором записей (Repeat) схемы.");
        var numbers = Numbers(nodes, repeat);
        switch (edit.Kind)
        {
            case SelectionEditKind.AddItem:
            {
                var number = numbers.Count == 0 ? 1 : numbers[numbers.Count - 1] + 1;
                if (number.ToString(CultureInfo.InvariantCulture).Length > RepeatItems.MaxNumberDigits)
                    throw new InvalidOperationException(name + "слишком много записей " + repeat.Id + "; новая запись не добавлена.");
                foreach (var cell in edit.Cells)
                    if (!repeat.Fields!.Any(field => string.Equals(field.Suffix, cell.Key, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("У записи " + repeat.Id + " нет поля «" + cell.Key + "» (поля: " +
                            string.Join(", ", repeat.Fields!.Select(field => field.Suffix)) + ").", nameof(edit));
                var at = nodes.FindLastIndex(node => RepeatItems.NumberOf(repeat, node.Id) != 0) + 1;
                if (at == 0) at = nodes.Count;
                foreach (var field in repeat.Fields!)
                {
                    var id = RepeatItems.ItemId(repeat.Id!, number, field.Suffix);
                    if (id.Length > SchemaValidator.MaxIdLength)
                        throw new InvalidOperationException(name + "Id " + id + " длиннее " + SchemaValidator.MaxIdLength + " символов; новая запись не добавлена.");
                    if (!schema.Parameters.TryGetValue(id, out var instance) || !schema.Parameters.IsDeclared(field.Parameter))
                        throw new InvalidOperationException("Поле «" + field.Suffix + "» записи " + repeat.Id + ": параметр " + field.Parameter +
                            " не описан в схеме или не является скалярным.");
                    var type = types.Find(instance.Type) ?? throw new InvalidOperationException("Тип \"" + instance.Type + "\" параметра " + field.Parameter + " не поддерживается.");
                    var given = edit.Cells.FirstOrDefault(cell => string.Equals(cell.Key, field.Suffix, StringComparison.OrdinalIgnoreCase));
                    var value = given.Key != null ? given.Value
                        : RepeatItems.DefaultValue(schema.Parameters[field.Parameter], id2 => id2 != null && schema.Enums.TryGetValue(id2, out var found) ? found : null, types)
                          ?? throw new InvalidOperationException("Поле «" + field.Suffix + "» записи " + repeat.Id + ": нет значения по умолчанию.");
                    nodes.Insert(at++, Pair(id, type.ToXData(Convert(instance, type, value, schema))));
                }
                return repeat;
            }
            case SelectionEditKind.RemoveLastItem:
                if (numbers.Count == 0) return null;
                var last = numbers[numbers.Count - 1];
                nodes.RemoveAll(node => RepeatItems.NumberOf(repeat, node.Id) == last);
                return repeat;
            case SelectionEditKind.DeleteItem:
                if (!numbers.Contains(edit.Number)) return null;
                nodes.RemoveAll(node => RepeatItems.NumberOf(repeat, node.Id) == edit.Number);
                // The following records are shifted down in the same transaction: the numbers stay without gaps.
                for (var i = 0; i < nodes.Count; i++)
                    if (RepeatItems.NumberOf(repeat, nodes[i].Id) is int number && number > edit.Number)
                        nodes[i] = Renumber(nodes[i], repeat, number - 1);
                return repeat;
            case SelectionEditKind.MoveItem:
            {
                if (!numbers.Contains(edit.Number)) return null;
                var other = edit.Number + edit.Offset;
                if (!numbers.Contains(other))
                    throw new InvalidOperationException(name + "нет записи «" + RepeatItems.Caption(repeat, Math.Max(other, 1)) + "»; запись «" +
                        RepeatItems.Caption(repeat, edit.Number) + "» не перемещена.");
                for (var i = 0; i < nodes.Count; i++)
                {
                    var number = RepeatItems.NumberOf(repeat, nodes[i].Id);
                    if (number == edit.Number) nodes[i] = Renumber(nodes[i], repeat, other);
                    else if (number == other) nodes[i] = Renumber(nodes[i], repeat, edit.Number);
                }
                return repeat;
            }
            default: throw new ArgumentException("Неизвестная операция.", nameof(edit));
        }
    }

    /// <summary>Record numbers stored in the object, ascending, without repetitions.</summary>
    internal static List<int> Numbers(IEnumerable<Node> nodes, SchemaParameter repeat) =>
        nodes.Select(node => RepeatItems.NumberOf(repeat, node.Id)).Where(number => number > 0).Distinct().OrderBy(number => number).ToList();

    private static int LastNumber(IEnumerable<Node> nodes, SchemaParameter repeat) => Numbers(nodes, repeat).LastOrDefault();

    /// <summary>A record exists when the object stores at least one of its fields.</summary>
    private static bool HasRecord(IEnumerable<Node> nodes, RepeatInstance instance) =>
        nodes.Any(node => RepeatItems.NumberOf(instance.Repeat, node.Id) == instance.Number);

    /// <summary><c>Spec_3_Name</c> → <c>Spec_2_Name</c>: the stored spelling of the prefix and the suffix is kept.</summary>
    private static Node Renumber(Node node, SchemaParameter repeat, int number)
    {
        var id = node.Id!;
        var prefix = repeat.Id!.Length;
        var end = id.IndexOf('_', prefix + 1);
        var renamed = id.Substring(0, prefix + 1) + number.ToString(CultureInfo.InvariantCulture) + id.Substring(end);
        var all = new List<DataValue>(node.All);
        all[0] = new DataValue(1000, renamed);
        return new Node(renamed, all, 1);
    }
}
