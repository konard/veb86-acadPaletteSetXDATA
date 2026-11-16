using System;
using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;
using static AcadPaletteSetXData.Core.Tests.RepeatItemsTests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #61, option B, in the VST: a <c>Repeat</c> is a node with records «Спецификация №1», «Спецификация №2», …,
/// each with the rows of its fields; «+ Добавить», «− Последняя», × (with the shift) and ▲ ▼ manage the records.
/// </summary>
public sealed class RepeatPropertyGridTests
{
    private static PropertyRow Node(PropertyGridViewModel grid, string id) =>
        All(grid.Roots).Single(row => row.Kind == PropertyRowKind.Collection && row.Id == id);

    private static PropertyRow Item(PropertyGridViewModel grid, string id, int number) =>
        Node(grid, id).Children.Single(row => row.Number == number);

    private static GroupEditingV2Tests.Drawing Specs(params EntityDataSnapshot[] snapshots) => new(RepeatCache(), snapshots);

    private static EntityDataSnapshot Device(string handle, params DataValue[][][] records) =>
        GroupEditingV2Tests.Device(handle, records.SelectMany(record => record).ToArray());

    [Fact]
    public void RecordsAreNumberedGroupsWithTheRowsOfTheirFields()
    {
        var drawing = Specs(Device("A", Spec(1, "1", "Шкаф", 2), Spec(2, "2", "Кабель", 10)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.Equal(new[] { "Общие", "Спецификация", "Соединения" }, grid.Roots.Select(row => row.Caption));
            var spec = Node(grid, "spec");
            Assert.Same(grid.Roots[1], spec); // The only collection of its group is the group itself.
            Assert.Equal("2 записи", spec.Value);
            Assert.Equal("●", spec.StateText);
            Assert.True(spec.ShowAdd && spec.CanAdd && spec.ShowRemoveLast && spec.CanRemoveLast);
            Assert.Equal(new[] { "Спецификация №1", "Спецификация №2" }, spec.Children.Select(row => row.Caption));

            var first = Item(grid, "spec", 1);
            Assert.Equal(PropertyRowKind.Item, first.Kind);
            Assert.True(first.IsHeader && first.ShowMove && first.ShowActions && first.CanDelete);
            Assert.False(first.CanMoveUp);
            Assert.True(first.CanMoveDown);
            Assert.False(Item(grid, "spec", 2).CanMoveDown);
            Assert.Equal(new[] { "Позиция", "Наименование", "Кол." }, first.Children.Select(row => row.Caption));
            Assert.Equal(new[] { "spec_1_pos", "spec_1_Name", "spec_1_col" }, first.Children.Select(row => row.Id));
            Assert.Equal("Шкаф", Row(grid, "spec_1_Name").Value);
            Assert.Equal("10.00", Row(grid, "spec_2_col").Value);
            Assert.StartsWith("1 · Шкаф · 2", first.Value);
            Assert.All(first.Children, row => Assert.False(row.CanDelete)); // A field is not removed alone.

            // A collection of the template without records: «0 записей», nothing to remove.
            var conn = Node(grid, "conn");
            Assert.Equal("0 записей", conn.Value);
            Assert.Equal("○", conn.StateText);
            Assert.Empty(conn.Children);
            Assert.True(conn.CanAdd);
            Assert.False(conn.CanRemoveLast);

            // The calculated parameter refers to the first record.
            Assert.Equal("1 Шкаф", Row(grid, "Spec_First").Value);
        }
    }

    [Fact]
    public void FieldOfARecordIsEditedAndCannotBeEmptied()
    {
        var drawing = Specs(Device("A", Spec(1, "1", "Шкаф", 2), Spec(2, "2", "Кабель", 10)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            var name = Row(grid, "spec_2_Name");
            name.EditText = "Лоток";
            Assert.True(grid.Commit(name));
            Assert.Equal(new object?[] { "Лоток" }, drawing.Stored("spec_2_Name"));
            Assert.Equal(1, drawing.Commits);

            var count = Row(grid, "spec_1_col");
            count.EditText = " ";
            Assert.False(grid.Commit(count));
            Assert.Contains("не может быть пустым", grid.Message);
            Assert.Equal(new object?[] { 2.0 }, drawing.Stored("spec_1_col"));
            Assert.Equal(1, drawing.Commits);
        }
    }

    [Fact]
    public void AddAndRemoveLastWorkPerObject()
    {
        var drawing = Specs(Device("A", Spec(1, "1", "Шкаф", 2), Spec(2, "2", "Кабель", 10)), Device("B", Spec(1, "7", "Щит", 1)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            var spec = Node(grid, "spec");
            Assert.True(spec.IsMixed);
            Assert.Equal("Разное", spec.Value);
            Assert.Equal("", Item(grid, "spec", 1).Note);
            Assert.Equal("есть в 1 из 2 объектов", Item(grid, "spec", 2).Note);

            Assert.True(grid.AddRow(spec)); // «+ Добавить» of a collection adds a record.
            Assert.Equal(new object?[] { "", null }, drawing.Stored("spec_3_pos")); // A: 1, 2 → 3.
            Assert.Equal(new object?[] { "2", "" }, drawing.Stored("spec_2_pos")); // B: 1 → 2.
            Assert.Equal(new object?[] { 1.0, 1.0 }, new[] { drawing.Stored("spec_3_col")[0], drawing.Stored("spec_2_col")[1] });
            Assert.Equal("spec_3_pos", grid.SelectedRow?.Id); // The first field of the new record is selected.

            Assert.True(grid.RemoveLastItem(Node(grid, "spec")));
            Assert.Equal(new object?[] { null, null }, drawing.Stored("spec_3_pos"));
            Assert.Equal(new object?[] { "2", null }, drawing.Stored("spec_2_pos"));
            Assert.Equal(new object?[] { "1", "7" }, drawing.Stored("spec_1_pos"));
            Assert.Equal(2, drawing.Commits);

            // Every record is removed: the collection of the template stays with «0 записей».
            Assert.True(grid.RemoveLastItem(Node(grid, "spec")));
            Assert.True(grid.RemoveLastItem(Node(grid, "spec")));
            Assert.Equal("0 записей", Node(grid, "spec").Value);
            Assert.False(Node(grid, "spec").CanRemoveLast);
            Assert.False(grid.RemoveLastItem(Node(grid, "spec")));
            Assert.Equal(4, drawing.Commits);
        }
    }

    [Fact]
    public void DeletingARecordShiftsTheFollowingAndReportsTheFormulas()
    {
        var drawing = Specs(Device("A", Spec(1, "1", "Шкаф", 2), Spec(2, "2", "Кабель", 10), Spec(3, "3", "Лоток", 4)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.True(grid.DeleteRow(Item(grid, "spec", 1)));
            Assert.Equal(new object?[] { "Кабель" }, drawing.Stored("spec_1_Name"));
            Assert.Equal(new object?[] { "Лоток" }, drawing.Stored("spec_2_Name"));
            Assert.Equal(new object?[] { null }, drawing.Stored("spec_3_Name"));
            Assert.Equal(1, drawing.Commits);
            Assert.False(grid.HasError);
            Assert.Contains("Spec_First", grid.Message); // The formula refers to spec_1_*, now another record.
            Assert.Equal("2 Кабель", Row(grid, "Spec_First").Value);

            // The last record changes no formula: no report.
            Assert.True(grid.DeleteRow(Item(grid, "spec", 2)));
            Assert.Equal(new object?[] { null }, drawing.Stored("spec_2_Name"));
            Assert.DoesNotContain("Spec_First", grid.Message);
        }
    }

    [Fact]
    public void MovingARecordSwapsTheNumbersAndKeepsItSelected()
    {
        var drawing = Specs(Device("A", Spec(1, "1", "Шкаф", 2), Spec(2, "2", "Кабель", 10), Spec(3, "3", "Лоток", 4)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.False(grid.MoveItem(Item(grid, "spec", 3), 1)); // No record below the last one.
            Assert.True(grid.MoveItem(Item(grid, "spec", 3), -1));
            Assert.Equal(new[] { "Шкаф", "Лоток", "Кабель" }, new[] { 1, 2, 3 }.Select(n => (string)drawing.Stored("spec_" + n + "_Name")[0]!));
            Assert.Equal(PropertyRowKind.Item, grid.SelectedRow?.Kind);
            Assert.Equal(2, grid.SelectedRow?.Number);
            Assert.DoesNotContain("Spec_First", grid.Message); // Records 2 and 3 are not used by formulas.

            Assert.True(grid.MoveItem(Item(grid, "spec", 2), -1));
            Assert.Equal(new[] { "Лоток", "Шкаф", "Кабель" }, new[] { 1, 2, 3 }.Select(n => (string)drawing.Stored("spec_" + n + "_Name")[0]!));
            Assert.Contains("Spec_First", grid.Message);
            Assert.Equal(1, grid.SelectedRow?.Number);
            Assert.Equal(2, drawing.Commits);
        }
    }

    [Fact]
    public void FieldParametersAreNotOfferedAndACollectionIsAddedWithItsFirstRecord()
    {
        var drawing = new GroupEditingV2Tests.Drawing(Object("C", "CABLE"));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key.StartsWith("Spec_", StringComparison.Ordinal) || option.Key.StartsWith("Conn_", StringComparison.Ordinal));
            Assert.Contains(grid.AvailableParameters, option => option.Key == "spec");
            Assert.DoesNotContain(All(grid.Roots), row => row.Kind == PropertyRowKind.Collection);

            Assert.True(grid.AddParameter("spec"));
            Assert.Equal(new object?[] { "" }, drawing.Stored("spec_1_pos"));
            var spec = Node(grid, "spec");
            Assert.Equal("1 запись", spec.Value);
            Assert.Equal("spec_1_pos", grid.SelectedRow?.Id);
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "spec");
        }
    }

    [Fact]
    public void ItemsTextAgreesWithTheNumber()
    {
        Assert.Equal("0 записей", PropertyGridViewModel.ItemsText(0));
        Assert.Equal("1 запись", PropertyGridViewModel.ItemsText(1));
        Assert.Equal("3 записи", PropertyGridViewModel.ItemsText(3));
        Assert.Equal("5 записей", PropertyGridViewModel.ItemsText(5));
        Assert.Equal("11 записей", PropertyGridViewModel.ItemsText(11));
        Assert.Equal("21 запись", PropertyGridViewModel.ItemsText(21));
        Assert.Equal("112 записей", PropertyGridViewModel.ItemsText(112));
    }
}
