using System.Collections.Generic;

namespace AcadPaletteSetXData.Core;

/// <summary>A material row with independent mixed flags and its original brand identity.</summary>
public sealed class MaterialItemViewModel : ObservableObject
{
    private string category = "";
    private string name = "";
    private string count = "1";
    private bool? isInSpec = true;
    private string comment = "";
    private bool isMixedValue;
    private bool isEditing;

    public string Category { get => category; set { Dirty.Add(nameof(Category)); SetProperty(ref category, value); ClearMixed(nameof(Category)); Notify(nameof(GroupCategory)); } }
    public string Name { get => name; set { Dirty.Add(nameof(Name)); SetProperty(ref name, value); ClearMixed(nameof(Name)); } }
    public string Count { get => count; set { Dirty.Add(nameof(Count)); SetProperty(ref count, value); ClearMixed(nameof(Count)); } }
    public bool? IsInSpec { get => isInSpec; set { Dirty.Add(nameof(IsInSpec)); SetProperty(ref isInSpec, value); ClearMixed(nameof(IsInSpec)); } }
    public string Comment { get => comment; set { Dirty.Add(nameof(Comment)); SetProperty(ref comment, value); ClearMixed(nameof(Comment)); } }
    public bool IsMixedValue { get => isMixedValue; set => SetProperty(ref isMixedValue, value); }
    public bool IsEditing { get => isEditing; set => SetProperty(ref isEditing, value); }
    internal HashSet<string> Dirty { get; } = new HashSet<string>();
    private readonly HashSet<string> mixed = new HashSet<string>();
    public string MaterialKey { get; internal set; } = "";
    private string? groupCategory;
    public string GroupCategory => groupCategory ?? (IsCategoryMixed ? "Разное" : Category);
    internal void FreezeGroupCategory() => groupCategory = GroupCategory;
    public bool IsCategoryMixed => mixed.Contains("Category");
    public bool IsNameMixed => mixed.Contains("Name");
    public bool IsCountMixed => mixed.Contains("Count");
    public bool IsInSpecMixed => mixed.Contains("IsInSpec");
    public bool IsCommentMixed => mixed.Contains("Comment");
    internal void MarkMixed(string field) { mixed.Add(field); IsMixedValue = true; Notify("Is" + field + "Mixed"); if (field == "Category") Notify(nameof(GroupCategory)); }
    private void ClearMixed(string field)
    {
        if (!mixed.Remove(field)) return;
        Notify("Is" + field + "Mixed");
        IsMixedValue = mixed.Count != 0;
    }
}
