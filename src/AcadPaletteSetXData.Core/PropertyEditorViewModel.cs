using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AcadPaletteSetXData.Core;

/// <summary>Editable layout state for stage two; persistence is provided in later stages.</summary>
public sealed class PropertyEditorViewModel : ObservableObject
{
    private string type = "";
    private string number = "";
    private string name = "";
    private string title = "";
    private string projectReference = "";

    public PropertyEditorViewModel()
    {
        AddMaterialCommand = new DelegateCommand(_ => Materials.Add(new MaterialItemViewModel
        {
            Category = Categories[0], IsEditing = true
        }));
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

    private bool OwnsMaterial(object? item) => item is MaterialItemViewModel material && Materials.Contains(material);
}
