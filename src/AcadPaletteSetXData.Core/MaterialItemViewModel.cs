namespace AcadPaletteSetXData.Core;

/// <summary>A material row in the UI draft. No AutoCAD database is accessed.</summary>
public sealed class MaterialItemViewModel : ObservableObject
{
    private string category = "";
    private string name = "";
    private string count = "1";
    private bool? isInSpec = true;
    private string comment = "";
    private bool isMixedValue;
    private bool isEditing;

    public string Category { get => category; set => SetProperty(ref category, value); }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Count { get => count; set => SetProperty(ref count, value); }
    public bool? IsInSpec { get => isInSpec; set => SetProperty(ref isInSpec, value); }
    public string Comment { get => comment; set => SetProperty(ref comment, value); }
    public bool IsMixedValue { get => isMixedValue; set => SetProperty(ref isMixedValue, value); }
    public bool IsEditing { get => isEditing; set => SetProperty(ref isEditing, value); }
}
