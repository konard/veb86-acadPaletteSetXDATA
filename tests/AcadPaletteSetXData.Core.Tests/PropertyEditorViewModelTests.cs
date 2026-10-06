using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class PropertyEditorViewModelTests
{
    [Fact]
    public void StartsWithAnEmptyDraftAndExposesTheSpecifiedCategories()
    {
        var model = new PaletteViewModel(enableDraftPreview: true);
        var editor = model.Properties;
        Assert.Same(editor, model.Tabs[0].Content);
        Assert.Empty(editor.Materials);
        Assert.Equal("", editor.Type);
        Assert.Equal("", editor.Number);
        Assert.Equal("", editor.Name);
        Assert.Equal("", editor.Title);
        Assert.Equal("", editor.ProjectReference);
        Assert.Equal(new[] { "Железобетонные элементы", "Стальные конструкции", "Линейная арматура" },
            editor.Categories);
    }

    [Fact]
    public void HeaderAndProjectFieldsNotifyOnceAndPreserveCustomText()
    {
        var editor = new PropertyEditorViewModel();
        var notifications = new List<string?>();
        editor.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        editor.Type = "Опора 0,4 кВ";
        editor.Type = "Опора 0,4 кВ";
        editor.Number = "021";
        editor.Name = "АО21";
        editor.Title = "Опора";
        editor.ProjectReference = "Проект/лист 1";
        Assert.Equal(new[] { "Type", "Number", "Name", "Title", "ProjectReference" }, notifications);
        Assert.Equal("Опора 0,4 кВ", editor.Type);
        Assert.Equal("021", editor.Number);
        Assert.Equal("АО21", editor.Name);
        Assert.Equal("Опора", editor.Title);
        Assert.Equal("Проект/лист 1", editor.ProjectReference);
    }

    [Fact]
    public void AddEditAndDeleteOperateOnTheRequestedDraftRowOnly()
    {
        var editor = new PropertyEditorViewModel();
        editor.AddMaterialCommand.Execute(null);
        var first = Assert.Single(editor.Materials);
        Assert.Equal(editor.Categories[0], first.Category);
        Assert.True(first.IsEditing);
        Assert.Equal("1", first.Count);
        Assert.True(first.IsInSpec);
        first.Name = "СВ110-5";
        first.Count = "1,2";
        first.Category = "Стальные конструкции";
        first.Comment = "Примечание";
        editor.EditMaterialCommand.Execute(first);
        Assert.False(first.IsEditing);
        editor.EditMaterialCommand.Execute(first);
        Assert.True(first.IsEditing);
        editor.AddMaterialCommand.Execute(null);
        var second = editor.Materials.Last();
        Assert.NotSame(first, second);
        editor.DeleteMaterialCommand.Execute(first);
        Assert.Same(second, Assert.Single(editor.Materials));
        Assert.Equal("СВ110-5", first.Name);
        Assert.Equal("1,2", first.Count);
        Assert.Equal("Примечание", first.Comment);
        Assert.False(editor.DeleteMaterialCommand.CanExecute(first));
        Assert.False(editor.EditMaterialCommand.CanExecute(new MaterialItemViewModel()));
        editor.DeleteMaterialCommand.Execute(first);
        Assert.Same(second, Assert.Single(editor.Materials));
    }

    [Fact]
    public void EveryMaterialFieldNotifiesAndSupportsNullableSpecificationState()
    {
        var material = new MaterialItemViewModel();
        var notifications = new List<string?>();
        material.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        material.Category = "Линейная арматура";
        material.Name = "CD35";
        material.Count = "Разное";
        material.IsInSpec = null;
        material.Comment = "Комментарий";
        material.IsMixedValue = true;
        material.IsEditing = true;
        material.Name = "CD35";
        Assert.Equal(new[] { "Category", "Name", "Count", "IsInSpec", "Comment", "IsMixedValue", "IsEditing" },
            notifications);
        Assert.Null(material.IsInSpec);
    }

    [Fact]
    public void DraftSurvivesThemeChangesAndTabSwitches()
    {
        var model = new PaletteViewModel(enableDraftPreview: true);
        model.Properties.Name = "АО21";
        model.Properties.AddMaterialCommand.Execute(null);
        var material = Assert.Single(model.Properties.Materials);
        model.SelectedTabIndex = 2;
        model.Theme = PaletteTheme.Light;
        model.SelectedTabIndex = 0;
        Assert.Equal("АО21", model.Properties.Name);
        Assert.Same(material, Assert.Single(model.Properties.Materials));
    }
}
