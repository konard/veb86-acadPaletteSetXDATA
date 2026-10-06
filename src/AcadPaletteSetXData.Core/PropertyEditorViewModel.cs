using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>Merges the selection; only explicit confirmation creates a database operation.</summary>
public sealed class PropertyEditorViewModel : ObservableObject
{
    internal static readonly string[] HeaderFields = { "Type", "Number", "Name", "Title", "ProjectReference" };
    internal static readonly string[] MaterialFields = { "Category", "Name", "Count", "IsInSpec", "Comment" };
    private string type = "", number = "", name = "", title = "", projectReference = "";
    private bool isEnabled = true, isReadOnly, canEdit = true;
    private bool selectionMode, writable;
    private readonly HashSet<string> mixed = new HashSet<string>();
    private readonly HashSet<string> dirty = new HashSet<string>();

    public PropertyEditorViewModel()
    {
        AddMaterialCommand = new DelegateCommand(_ =>
        {
            var material = new MaterialItemViewModel { Category = Categories[0], IsEditing = true };
            if (selectionMode)
            {
                // A unique initial brand gives the newly added row a stable group identity.
                var index = 1;
                do { material.Name = "Новый материал " + index++; } while (Materials.Any(row => row.MaterialKey == material.Name));
                EditRequested?.Invoke(this, SelectionEdit.Add(material));
                var added = Materials.FirstOrDefault(row => row.MaterialKey == material.Name);
                if (added != null) added.IsEditing = true;
            }
            else Materials.Add(material);
        }, _ => CanEdit);
        EditMaterialCommand = new DelegateCommand(item =>
        {
            var material = (MaterialItemViewModel)item!;
            material.IsEditing = !material.IsEditing;
        }, OwnsMaterial);
        DeleteMaterialCommand = new DelegateCommand(item =>
        {
            var material = (MaterialItemViewModel)item!;
            if (selectionMode) EditRequested?.Invoke(this, SelectionEdit.Delete(material.MaterialKey));
            else Materials.Remove(material);
        }, OwnsMaterial);
        Materials.CollectionChanged += (_, __) => RaiseCommands();
    }

    public event EventHandler<SelectionEdit>? EditRequested;
    public IReadOnlyList<string> Types { get; } = Array.AsReadOnly(new[] { "Опора 0,4 кВ", "Устройство", "Кабель", "Суперлиния" });
    public IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(new[] { "Железобетонные элементы", "Стальные конструкции", "Линейная арматура" });
    public ObservableCollection<MaterialItemViewModel> Materials { get; } = new ObservableCollection<MaterialItemViewModel>();
    public DelegateCommand AddMaterialCommand { get; }
    public DelegateCommand EditMaterialCommand { get; }
    public DelegateCommand DeleteMaterialCommand { get; }
    public string Type { get => type; set => Change(ref type, value, nameof(Type)); }
    public string Number { get => number; set => Change(ref number, value, nameof(Number)); }
    public string Name { get => name; set => Change(ref name, value, nameof(Name)); }
    public string Title { get => title; set => Change(ref title, value, nameof(Title)); }
    public string ProjectReference { get => projectReference; set => Change(ref projectReference, value, nameof(ProjectReference)); }
    public bool IsTypeMixed => mixed.Contains("Type");
    public bool IsNumberMixed => mixed.Contains("Number");
    public bool IsNameMixed => mixed.Contains("Name");
    public bool IsTitleMixed => mixed.Contains("Title");
    public bool IsProjectReferenceMixed => mixed.Contains("ProjectReference");
    public bool IsEnabled { get => isEnabled; private set => SetProperty(ref isEnabled, value); }
    public bool IsReadOnly { get => isReadOnly; private set => SetProperty(ref isReadOnly, value); }
    public bool CanEdit { get => canEdit; private set => SetProperty(ref canEdit, value); }

    private void Change(ref string storage, string value, string field)
    {
        dirty.Add(field);
        SetProperty(ref storage, value, field);
        if (mixed.Remove(field)) Notify("Is" + field + "Mixed");
    }

    public void CommitField(string field)
    {
        if (!CanEdit || !selectionMode || !dirty.Remove(field) || !HeaderFields.Contains(field)) return;
        EditRequested?.Invoke(this, SelectionEdit.Header(field, (string)GetType().GetProperty(field)!.GetValue(this)!));
    }

    public void CommitMaterialField(MaterialItemViewModel row, string field)
    {
        if (!OwnsMaterial(row) || !selectionMode || !row.Dirty.Remove(field) || !MaterialFields.Contains(field)) return;
        var value = typeof(MaterialItemViewModel).GetProperty(field)!.GetValue(row);
        EditRequested?.Invoke(this, SelectionEdit.MaterialField(row.MaterialKey, field, value?.ToString() ?? ""));
    }

    internal void SetWritable(bool value)
    {
        writable = value;
        if (selectionMode) { IsReadOnly = !value; CanEdit = IsEnabled && value; RaiseCommands(); }
    }

    internal void LoadSelection(IReadOnlyList<ParsedEntityData> entities)
    {
        selectionMode = true;
        IsEnabled = entities.Count != 0; IsReadOnly = !writable; CanEdit = IsEnabled && writable;
        mixed.Clear();
        foreach (var field in HeaderFields)
        {
            var values = entities.Select(entity => entity.Properties.TryGetValue(field, out var value) ? value : null).ToArray();
            var differs = values.Distinct(StringComparer.Ordinal).Skip(1).Any();
            GetType().GetProperty(field)!.SetValue(this, differs || values.Length == 0 ? "" : values[0] ?? "");
            if (differs) mixed.Add(field);
            Notify("Is" + field + "Mixed");
        }
        var editing = new HashSet<string>(Materials.Where(row => row.IsEditing).Select(row => row.IsNameMixed ? row.MaterialKey : row.Name), StringComparer.Ordinal);
        dirty.Clear(); Materials.Clear();
        // Match materials by exact brand. Duplicate occurrences contribute to comparison and are edited together.
        var materialGroups = entities.Select(entity => entity.Materials.GroupBy(row => row.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)).ToArray();
        foreach (var group in entities.SelectMany(entity => entity.Materials).GroupBy(row => row.Name, StringComparer.Ordinal))
        {
            var key = group.Key;
            var values = group.ToArray();
            var occurrences = materialGroups.Select(groups => groups.TryGetValue(key, out var count) ? count : 0).ToArray();
            var missing = occurrences.Contains(0) || occurrences.Distinct().Skip(1).Any();
            var merged = new MaterialItemViewModel { MaterialKey = key };
            foreach (var field in MaterialFields)
            {
                var property = typeof(MaterialItemViewModel).GetProperty(field)!;
                var differs = missing || values.Select(row => property.GetValue(row)).Distinct().Skip(1).Any();
                property.SetValue(merged, differs ? (field == "IsInSpec" ? null : "") : property.GetValue(values[0]));
                if (differs) merged.MarkMixed(field);
            }
            // Keep a focused row in place while typing; regroup only after confirmation and refresh.
            merged.FreezeGroupCategory();
            merged.IsEditing = editing.Contains(key);
            merged.Dirty.Clear(); Materials.Add(merged);
        }
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        AddMaterialCommand.RaiseCanExecuteChanged(); EditMaterialCommand.RaiseCanExecuteChanged(); DeleteMaterialCommand.RaiseCanExecuteChanged();
    }
    private bool OwnsMaterial(object? item) => CanEdit && item is MaterialItemViewModel material && Materials.Contains(material);
}
