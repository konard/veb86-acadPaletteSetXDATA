using System;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

// XAML uses the actual model types in both the modular and single-assembly builds.
public static class ViewModelTypes
{
    public static Type Properties => typeof(PropertyEditorViewModel);
    public static Type Node => typeof(XDataNode);
    public static Type Selection => typeof(SelectionViewModel);
    public static Type Tab => typeof(PaletteTab);
}
