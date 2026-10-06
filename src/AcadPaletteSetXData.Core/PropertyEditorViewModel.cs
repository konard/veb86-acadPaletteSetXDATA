using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AcadPaletteSetXData.Core;

/// <summary>Property projection for stage three, with an editable local draft for standalone previews.</summary>
public sealed class PropertyEditorViewModel : ObservableObject
{
    private string type = "";
    private string number = "";
    private string name = "";
    private string title = "";
    private string projectReference = "";
    private bool isEnabled = true;
    private bool isReadOnly;
    private bool canEdit = true;

    public PropertyEditorViewModel()
    {
        AddMaterialCommand = new DelegateCommand(_ => Materials.Add(new MaterialItemViewModel
        {
            Category = Categories[0], IsEditing = true
        }), _ => CanEdit);
        EditMaterialCommand = new DelegateCommand(
            item => { var material = (MaterialItemViewModel)item!; material.IsEditing = !material.IsEditing; },
            OwnsMaterial);
        DeleteMaterialCommand = new DelegateCommand(item => Materials.Remove((MaterialItemViewModel)item!),
            OwnsMaterial);
        Materials.CollectionChanged += (_, __) =>
        {
            EditMaterialCommand.RaiseCanExecuteChanged();
            DeleteMaterialCommand.RaiseCanExecuteChanged();
        };
    }

    public IReadOnlyList<string> Types { get; } = Array.AsReadOnly(new[]
    {
        "Опора 0,4 кВ", "Устройство", "Кабель", "Суперлиния"
    });
    public IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(new[]
    {
        "Железобетонные элементы", "Стальные конструкции", "Линейная арматура"
    });
    public ObservableCollection<MaterialItemViewModel> Materials { get; } = new ObservableCollection<MaterialItemViewModel>();
    public DelegateCommand AddMaterialCommand { get; }
    public DelegateCommand EditMaterialCommand { get; }
    public DelegateCommand DeleteMaterialCommand { get; }
    public string Type { get => type; set => SetProperty(ref type, value); }
    public string Number { get => number; set => SetProperty(ref number, value); }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Title { get => title; set => SetProperty(ref title, value); }
    public string ProjectReference { get => projectReference; set => SetProperty(ref projectReference, value); }
    public bool IsEnabled { get => isEnabled; private set => SetProperty(ref isEnabled, value); }
    public bool IsReadOnly { get => isReadOnly; private set => SetProperty(ref isReadOnly, value); }
    public bool CanEdit { get => canEdit; private set => SetProperty(ref canEdit, value); }

    internal void LoadSelection(ParsedEntityData? entity)
    {
        IsEnabled = entity != null;
        IsReadOnly = true;
        CanEdit = IsEnabled && !IsReadOnly;
        string Get(string key) => entity != null && entity.Properties.TryGetValue(key, out var value) ? value : "";
        Type = Get("Type"); Number = Get("Number"); Name = Get("Name");
        Title = Get("Title"); ProjectReference = Get("ProjectReference");
        Materials.Clear();
        if (entity != null) foreach (var material in entity.Materials) Materials.Add(material);
        AddMaterialCommand.RaiseCanExecuteChanged();
        EditMaterialCommand.RaiseCanExecuteChanged();
        DeleteMaterialCommand.RaiseCanExecuteChanged();
    }

    private bool OwnsMaterial(object? item) => CanEdit && item is MaterialItemViewModel material && Materials.Contains(material);
}
