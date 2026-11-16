using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;
using static AcadPaletteSetXData.Core.Tests.RepeatItemsTests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #61, option B, in the schema editor: a <c>Repeat</c> is created with the type «Repeat» and the fields
/// «суффикс=Id параметра»; a collection whose records are stored in objects is neither deleted nor renamed.
/// </summary>
public sealed class RepeatSchemaEditorTests
{
    private static ParameterSchema Schema()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(RepeatSchemaPath));
        Assert.False(result.HasErrors);
        return result.Schema!;
    }

    private static SchemaEditorViewModel Editor(params EntityDataSnapshot[] objects) =>
        new(Schema(), SchemaUsage.Scan(objects.Select(item => item.XData)), objects, CultureInfo.InvariantCulture);

    [Fact]
    public void UsageCountsObjectsWithRecordsOfACollection()
    {
        var usage = SchemaUsage.Scan(new[]
        {
            Device("A", Spec(1, "1", "Шкаф", 1).Concat(Spec(2, "2", "Кабель", 1)).ToArray()).XData,
            Device("B", Spec(3, "3", "Лоток", 1)).XData,
            Device("C", Pair("x_1_2_pos", "?")).XData,
            Device("D", Pair("Device_Type", "Щит")).XData,
        });
        Assert.Equal(2, usage.CountRepeat("spec")); // Each object once.
        Assert.Equal(2, usage.CountRepeat("SPEC"));
        Assert.Equal(0, usage.CountRepeat("conn"));
        Assert.Equal(1, usage.CountRepeat("x")); // x_1_2_pos may be a record of x or of x_1.
        Assert.Equal(1, usage.CountRepeat("x_1"));
        Assert.Equal(0, usage.CountRepeat("Device"));
        Assert.Equal(0, usage.CountRepeat(null));
        Assert.Equal(0, usage.Count(SchemaSection.Parameter, "spec")); // The Repeat has no pair of its own.
    }

    [Fact]
    public void NewRepeatGetsItsFieldsAsSuffixAndParameter()
    {
        var editor = Editor();
        var item = editor.AddParameter();
        item.Id = "load";
        item.Caption = "Нагрузка №{n}";
        item.Type = "Repeat";
        Assert.True(item.IsRepeat);
        Assert.False(item.IsTable);
        Assert.Contains(editor.Issues, issue => issue.Field == "fields"); // A Repeat without fields is an error.
        Assert.False(editor.CanApply);

        item.FieldsText = "name=Spec_Name; count = Spec_Count";
        Assert.Equal("name=Spec_Name, count=Spec_Count", item.FieldsText);
        Assert.Equal(new[] { ("name", "Spec_Name"), ("count", "Spec_Count") }, item.Model.Fields!.Select(field => (field.Suffix, field.Parameter)));
        Assert.False(editor.HasErrors, string.Join("\n", editor.Issues.Select(issue => issue.Message)));
        Assert.True(editor.CanApply);
        // In the order of the schema: the new parameter follows the selected one (the first).
        Assert.Equal("Используется: набор записей load, набор записей spec", editor.FindParameter("Spec_Name")!.ReferencesText);

        // A part without «=» stays in the box; the model keeps the last correct fields.
        item.FieldsText = "name=Spec_Name, count";
        Assert.Equal("name=Spec_Name, count", item.FieldsText);
        Assert.Equal(2, item.Model.Fields!.Count);
        Assert.Contains(editor.Issues, issue => issue.Field == "fields" && issue.Message.Contains("«count»"));
        Assert.False(editor.CanApply);
        item.FieldsText = "name=Spec_Name";
        Assert.False(editor.HasErrors);
        var saved = ParameterSchemaJson.Load(ParameterSchemaJson.Write(editor.Draft)).Schema!.FindParameter("load")!;
        Assert.Equal(("name", "Spec_Name"), (saved.Fields!.Single().Suffix, saved.Fields!.Single().Parameter));
    }

    [Fact]
    public void RenamingAFieldParameterRenamesTheFieldAndDeletingItIsRefused()
    {
        var editor = Editor(Device("A", Spec(1, "1", "Шкаф", 1)));
        var name = editor.FindParameter("Spec_Name")!;
        Assert.Equal(0, name.UsageCount); // Objects store spec_1_Name, not Spec_Name.
        Assert.Contains("набор записей spec", name.ReferencesText);
        Assert.False(editor.DeleteParameter(name));
        Assert.Contains("набор записей spec", editor.Status);

        name.Id = "Spec_Title";
        Assert.Equal("Spec_Title", editor.Draft.FindParameter("spec")!.Fields!.Single(field => field.Suffix == "Name").Parameter);
        Assert.Equal("pos=Spec_Pos, Name=Spec_Title, col=Spec_Count", editor.FindParameter("spec")!.FieldsText);
        Assert.False(editor.HasErrors, string.Join("\n", editor.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void CollectionWithStoredRecordsIsNeitherDeletedNorRenamed()
    {
        var editor = Editor(Device("A", Spec(1, "1", "Шкаф", 1)), Device("B", Spec(2, "2", "Кабель", 1)), Device("C"));
        var spec = editor.FindParameter("spec")!;
        Assert.Equal(2, spec.UsageCount);
        Assert.Equal("Хранится в объектах чертежа: 2", spec.UsageText);
        Assert.False(editor.DeleteParameter(spec));
        Assert.Equal("Набор записей spec хранится в объектах чертежа (2) — удаление запрещено.", editor.Status);
        spec.Id = "specification";
        Assert.Equal("spec", spec.Model.Id);
        Assert.Equal("Набор записей spec хранится в объектах чертежа (2) — менять Id нельзя.", editor.Status);
        spec.Id = "SPEC"; // Ids are case-insensitive: the stored records still match.
        Assert.Equal("SPEC", spec.Model.Id);

        // conn has no records in the drawing: it may be deleted once the template no longer lists it.
        var conn = editor.FindParameter("conn")!;
        Assert.Equal(0, conn.UsageCount);
        Assert.False(editor.DeleteParameter(conn));
        Assert.Contains("шаблон DEVICE", editor.Status);
        editor.SelectedTemplate = editor.FindTemplate("DEVICE");
        editor.RemoveFromTemplate(editor.SelectedTemplate!.Parameters.Single(parameter => parameter.Id == "conn"));
        Assert.True(editor.DeleteParameter(conn));
        Assert.Null(editor.Draft.FindParameter("conn"));
    }
}
